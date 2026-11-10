import * as HostProcess from "@t3tools/shared/HostProcess";
import { pathSetupCommand } from "@t3tools/shared/shell";
import * as Console from "effect/Console";
import * as Effect from "effect/Effect";
import * as FileSystem from "effect/FileSystem";
import * as Option from "effect/Option";
import * as Path from "effect/Path";
import * as Schema from "effect/Schema";
import { Command } from "effect/cli";

import packageJson from "../../package.json" with { type: "json" };
import { detectCliRunner } from "./invocation.ts";
import {
  generatedWindowsShimTarget,
  isVersionExecutable,
  recordPinnedRuntimeLauncher,
} from "../cloud/pinnedRuntime.ts";

export class CliInstallError extends Schema.TaggedError<CliInstallError>()("CliInstallError", {
  reason: Schema.String,
  cause: Schema.optional(Schema.Defect()),
}) {
  override get message(): string {
    return this.reason;
  }
}

/** `<T3 home>/runtime/versions/<version>/t3`: the layout the install scripts and npm launcher share. */
const standaloneRuntime = Effect.gen(function* () {
  const path = yield* Path.Path;
  if (!(yield* HostProcess.IsExecutable)) return Option.none();
  const executable = yield* HostProcess.ExecutablePath;
  const versionsDir = path.dirname(path.dirname(executable));
  const runtimeDir = path.dirname(versionsDir);
  if (path.basename(versionsDir) !== "versions" || path.basename(runtimeDir) !== "runtime") {
    return Option.none();
  }
  return Option.some({ executable, versionsDir, runtimeDir });
});

/** Links this runtime as the `t3` command the way the install scripts do, replacing only our own link. */
export const installCli = Effect.fn("cli.install.install")(function* () {
  const fs = yield* FileSystem.FileSystem;
  const path = yield* Path.Path;
  const platform = yield* HostProcess.Platform;
  const environment = yield* HostProcess.Environment;
  const home = yield* HostProcess.HomeDirectory;
  const runtime = yield* standaloneRuntime;
  const windows = platform === "win32";
  if (Option.isNone(runtime)) {
    const script = windows
      ? "irm https://t3.codes/install.ps1 | iex"
      : "curl -fsSL https://t3.codes/install.sh | sh";
    return yield* new CliInstallError({
      reason: `Only the standalone t3 can install itself. Use \`${script}\`, or Settings in the desktop app.`,
    });
  }
  const { executable, versionsDir, runtimeDir } = runtime.value;
  // cmd.exe expands `%` and reads the shim in the OEM code page, so such a path would not run.
  if (windows && /[%\u0080-\uffff]/.test(executable)) {
    return yield* new CliInstallError({
      reason: `A t3.cmd cannot run ${executable}, because the path has % or non-ASCII characters. Keep using npx t3.`,
    });
  }
  // Absolute, so the PATH hint never adds a folder that changes with the working directory.
  const binDir = path.resolve(
    environment.T3CODE_INSTALL_BIN_DIR?.trim() || path.join(home, ".local", "bin"),
  );
  const name = windows ? "t3.cmd" : "t3";
  const link = path.join(binDir, name);
  const failed = (reason: string) => (cause: unknown) => new CliInstallError({ reason, cause });

  // Resolve fully so a link through a symlinked T3 home still counts as ours; a dangling one
  // (a pruned version) falls back to its own text.
  const realVersionsDir = yield* fs
    .realPath(versionsDir)
    .pipe(Effect.orElseSucceed(() => versionsDir));
  const current = windows
    ? yield* fs.readFileString(link).pipe(Effect.map(generatedWindowsShimTarget), Effect.option)
    : yield* fs.readLink(link).pipe(
        Effect.flatMap((target) =>
          fs.realPath(link).pipe(Effect.orElseSucceed(() => path.resolve(binDir, target))),
        ),
        Effect.option,
      );
  // A dangling symlink is occupied too: writing the shim would follow it.
  const occupied =
    Option.isSome(current) ||
    (yield* fs.exists(link)) ||
    Option.isSome(yield* fs.readLink(link).pipe(Effect.option));
  if (
    occupied &&
    !(
      Option.isSome(current) &&
      current.value !== undefined &&
      (isVersionExecutable(path, versionsDir, current.value) ||
        isVersionExecutable(path, realVersionsDir, current.value) ||
        // This home's desktop launcher, which install.sh also replaces.
        current.value === path.join(path.dirname(runtimeDir), "bin", name))
    )
  ) {
    return yield* new CliInstallError({
      reason: `Another t3 is already at ${link}. Remove it, then run this again.`,
    });
  }

  yield* fs
    .makeDirectory(binDir, { recursive: true })
    .pipe(Effect.mapError(failed(`Could not create ${binDir}.`)));
  // An empty spot is created exclusively, so a file that appears after the check above is kept.
  if (windows) {
    yield* fs
      .writeFileString(link, `@echo off\r\n"${executable}" %*`, occupied ? {} : { flag: "wx" })
      .pipe(Effect.mapError(failed(`Could not write ${link}.`)));
  } else if (!occupied) {
    yield* fs.symlink(executable, link).pipe(Effect.mapError(failed(`Could not link ${link}.`)));
  } else {
    // Swap a temporary link into place so a running `t3` never sees the command missing.
    const staging = `${link}.${process.pid}.tmp`;
    // Clean up only a staging link this call made, never a file that was already there.
    yield* fs
      .symlink(executable, staging)
      .pipe(
        Effect.andThen(
          fs
            .rename(staging, link)
            .pipe(Effect.tapError(() => fs.remove(staging, { force: true }).pipe(Effect.ignore))),
        ),
        Effect.mapError(failed(`Could not link ${link}.`)),
      );
  }

  // Pruning keeps what recorded launchers point at; a background service cannot see binDir otherwise.
  const recorded = path.join(
    yield* fs.realPath(binDir).pipe(Effect.orElseSucceed(() => binDir)),
    name,
  );
  yield* recordPinnedRuntimeLauncher({
    baseDir: path.dirname(runtimeDir),
    launcher: recorded,
  }).pipe(
    Effect.catch(() =>
      Console.error(
        `Warning: could not record ${recorded} in ${path.join(runtimeDir, "launchers")}. Cleanup may remove the version it runs.`,
      ),
    ),
  );

  // resolve() drops trailing separators, so `~/.local/bin/` on PATH still matches.
  const normalize = (value: string) =>
    windows ? path.resolve(value).toLowerCase() : path.resolve(value);
  const same = (entry: string) => entry !== "" && normalize(entry) === normalize(binDir);
  const entries = (environment.PATH ?? environment.Path ?? "").split(windows ? ";" : ":");
  const onPath = entries.some(same);
  // A t3 earlier on PATH runs instead, and an added folder goes last. Runner caches (npx's own
  // .bin) are not installs.
  const firstOther = Effect.gen(function* () {
    for (const entry of entries) {
      if (same(entry)) return undefined;
      if (!path.isAbsolute(entry) || detectCliRunner(`${entry}/`) !== null) continue;
      for (const name of windows ? ["t3.cmd", "t3.exe"] : ["t3"]) {
        const candidate = path.join(entry, name);
        if (yield* fs.exists(candidate).pipe(Effect.orElseSucceed(() => false))) return candidate;
      }
    }
    return undefined;
  });
  const shadowedBy = yield* firstOther;
  return {
    link,
    onPath: onPath && shadowedBy === undefined,
    shadowedBy,
    pathCommand:
      onPath || shadowedBy !== undefined
        ? undefined
        : windows
          ? // PowerShell; it covers this window only, since changing the saved PATH would flatten
            // its %VAR% entries. A `;` would split the folder.
            binDir.includes(";")
            ? undefined
            : `$env:Path += ';${binDir.replaceAll("'", "''")}'`
          : pathSetupCommand({
              directory: binDir,
              home,
              shell: environment.SHELL,
              platform,
              configHome: environment.XDG_CONFIG_HOME,
              zdotdir: environment.ZDOTDIR,
            }),
    binDir,
  };
});

const reportInstalled = Effect.fn("cli.install.report")(function* (
  installed: Effect.Success<ReturnType<typeof installCli>>,
) {
  yield* Console.log(`Installed t3 ${packageJson.version} at ${installed.link}.`);
  if (installed.shadowedBy !== undefined) {
    return yield* Console.log(
      `Another t3 at ${installed.shadowedBy} runs first, so run ${installed.link} to use this one.`,
    );
  }
  if (installed.onPath) return yield* Console.log("Run `t3` to get started.");
  if (installed.pathCommand === undefined) {
    return yield* Console.log(`Add ${installed.binDir} to your PATH, then run \`t3\`.`);
  }
  if ((yield* HostProcess.Platform) === "win32") {
    yield* Console.log(
      `${installed.binDir} is not on your PATH yet. In this PowerShell window, run:`,
    );
    yield* Console.log(`  ${installed.pathCommand}`);
    return yield* Console.log("Then run `t3`. For new windows, add the folder to your PATH.");
  }
  yield* Console.log(`${installed.binDir} is not on your PATH yet. Add it with:`);
  yield* Console.log(`  ${installed.pathCommand}`);
  yield* Console.log("Then open a new terminal and run `t3`.");
});

export const installCommand = Command.make("install").pipe(
  Command.withDescription("Install this version as the `t3` command."),
  Command.withHandler(() => installCli().pipe(Effect.flatMap(reportInstalled))),
);
