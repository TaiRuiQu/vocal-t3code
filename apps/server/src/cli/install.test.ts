import * as NodeServices from "@effect/platform-node/NodeServices";
import { assert, it } from "@effect/vitest";
import * as HostProcess from "@t3tools/shared/HostProcess";
import * as Effect from "effect/Effect";
import * as FileSystem from "effect/FileSystem";
import * as Path from "effect/Path";

import packageJson from "../../package.json" with { type: "json" };
import { installCli } from "./install.ts";

const VERSION = packageJson.version;

/** A standalone runtime in a temp T3 home, started by the npm launcher from an npx cache. */
const setup = Effect.gen(function* () {
  const fs = yield* FileSystem.FileSystem;
  const path = yield* Path.Path;
  const root = yield* fs.makeTempDirectoryScoped({ prefix: "t3-install-" });
  const runtimeDir = path.join(root, ".t3/runtime");
  const executable = path.join(runtimeDir, "versions", VERSION, "t3");
  yield* fs.makeDirectory(path.dirname(executable), { recursive: true });
  yield* fs.writeFileString(executable, "");
  const npmPackage = path.join(root, ".npm/_npx/abc/node_modules/t3");
  yield* fs.makeDirectory(path.join(npmPackage, "bin"), { recursive: true });
  yield* fs.writeFileString(
    path.join(npmPackage, "package.json"),
    JSON.stringify({ name: "t3", version: VERSION }),
  );
  const binDir = path.join(root, "bin");
  const provide = <A, E, R>(effect: Effect.Effect<A, E, R>, env: NodeJS.ProcessEnv = {}) =>
    effect.pipe(
      Effect.provideService(HostProcess.Platform, "linux"),
      Effect.provideService(HostProcess.HomeDirectory, root),
      Effect.provideService(HostProcess.IsExecutable, true),
      Effect.provideService(HostProcess.ExecutablePath, executable),
      Effect.provideService(HostProcess.Environment, {
        PATH: "/usr/bin",
        SHELL: "/bin/zsh",
        T3CODE_INSTALL_BIN_DIR: binDir,
        T3CODE_NPM_LAUNCHER_PATH: path.join(npmPackage, "bin/t3.mjs"),
        ...env,
      }),
    );
  return { root, npmPackage, runtimeDir, executable, link: path.join(binDir, "t3"), provide };
});

it.layer(NodeServices.layer)("t3 install", (it) => {
  it.effect("links the running runtime, records it, and never replaces another t3", () =>
    Effect.gen(function* () {
      const fs = yield* FileSystem.FileSystem;
      const path = yield* Path.Path;
      const { root, runtimeDir, executable, link, provide } = yield* setup;

      const older = path.join(runtimeDir, "versions/0.0.1/t3");
      yield* fs.makeDirectory(path.dirname(link), { recursive: true });
      yield* fs.symlink(older, link);
      const installed = yield* provide(installCli());
      assert.equal(yield* fs.readLink(link), executable);
      assert.isFalse(installed.onPath);
      assert.include(installed.pathCommand, "$HOME/bin");

      // A link written through a symlinked T3 home is still ours.
      const alias = path.join(root, "alias");
      yield* fs.symlink(path.join(root, ".t3"), alias);
      yield* fs.remove(link);
      yield* fs.symlink(path.join(alias, "runtime/versions", VERSION, "t3"), link);
      const onPath = yield* provide(installCli(), { PATH: `/usr/bin:${path.join(root, "bin")}/` });
      assert.isTrue(onPath.onPath);
      // Another t3 earlier on PATH still runs first, so `t3` would not run this one.
      const otherBin = path.join(root, "other-bin");
      yield* fs.makeDirectory(otherBin);
      yield* fs.writeFileString(path.join(otherBin, "t3"), "");
      const shadowed = yield* provide(installCli(), {
        PATH: `${otherBin}:${path.join(root, "bin")}`,
      });
      assert.isFalse(shadowed.onPath);
      assert.equal(shadowed.shadowedBy, path.join(otherBin, "t3"));
      assert.isUndefined(shadowed.pathCommand);
      const recorded = yield* fs.readFileString(path.join(runtimeDir, "launchers"));
      assert.equal(recorded, `\n${yield* fs.realPath(path.join(root, "bin"))}/t3\n`);

      yield* fs.remove(link);
      yield* fs.writeFileString(link, "#!/bin/sh\n");
      const error = yield* provide(installCli()).pipe(Effect.flip);
      assert.include(error.message, "Another t3 is already at");
      assert.equal(yield* fs.readFileString(link), "#!/bin/sh\n");

      // Only `<version>/t3` is ours, not another program inside a version.
      yield* fs.remove(link);
      yield* fs.symlink(path.join(runtimeDir, "versions/0.0.1/node"), link);
      const sibling = yield* provide(installCli()).pipe(Effect.flip);
      assert.include(sibling.message, "Another t3 is already at");
      // This home's desktop launcher is replaced, as install.sh does.
      yield* fs.remove(link);
      yield* fs.symlink(path.join(root, ".t3/bin/t3"), link);
      yield* provide(installCli());
      assert.equal(yield* fs.readLink(link), executable);

      // A failed swap over our own link leaves a staging path it did not create alone.
      yield* fs.remove(link);
      yield* fs.symlink(older, link);
      const staging = `${link}.${process.pid}.tmp`;
      yield* fs.writeFileString(staging, "someone else's\n");
      const linkFailed = yield* provide(installCli()).pipe(Effect.flip);
      assert.include(linkFailed.message, "Could not link");
      assert.equal(yield* fs.readFileString(staging), "someone else's\n");
      yield* fs.remove(staging);

      // An empty spot is created exclusively: a file that appears first is kept.
      yield* fs.remove(link);
      const racing = installCli().pipe(
        Effect.provideService(FileSystem.FileSystem, {
          ...fs,
          symlink: (target, path) =>
            fs
              .writeFileString(path, "#!/bin/sh\necho raced\n")
              .pipe(Effect.andThen(fs.symlink(target, path))),
        }),
      );
      assert.include((yield* provide(racing).pipe(Effect.flip)).message, "Could not link");
      assert.equal(yield* fs.readFileString(link), "#!/bin/sh\necho raced\n");
      yield* fs.remove(link);

      // Windows writes a t3.cmd: only over its own, and only for paths cmd can run.
      const shim = path.join(root, "bin", "t3.cmd");
      const windows = <A, E, R>(effect: Effect.Effect<A, E, R>) =>
        provide(effect.pipe(Effect.provideService(HostProcess.Platform, "win32")));
      const desktopShim =
        '@echo off\r\nrem Written by T3 Code\r\n"C:\\T3 Code\\T3 Code.exe" "x" %*\r\n';
      yield* fs.writeFileString(shim, desktopShim);
      const foreignShim = yield* windows(installCli()).pipe(Effect.flip);
      assert.include(foreignShim.message, "Another t3 is already at");
      assert.equal(yield* fs.readFileString(shim), desktopShim);
      // A user's wrapper that also runs a managed t3.exe is still theirs.
      const wrapper = `@echo off\r\nset FOO=1\r\n"${path.join(runtimeDir, "versions/0.0.1/t3.exe")}" %*`;
      yield* fs.writeFileString(shim, wrapper);
      yield* windows(installCli()).pipe(Effect.flip);
      assert.equal(yield* fs.readFileString(shim), wrapper);
      // A dangling symlink is not free; writing through it would land outside the bin folder.
      const elsewhere = path.join(root, "elsewhere.cmd");
      yield* fs.remove(shim);
      yield* fs.symlink(elsewhere, shim);
      yield* windows(installCli()).pipe(Effect.flip);
      assert.isFalse(yield* fs.exists(elsewhere));
      yield* fs.remove(shim);
      yield* fs.writeFileString(
        shim,
        `@echo off\r\n"${path.join(runtimeDir, "versions/0.0.1/t3.exe")}" %*`,
      );
      const windowsInstalled = yield* windows(installCli());
      assert.equal(yield* fs.readFileString(shim), `@echo off\r\n"${executable}" %*`);
      // A PowerShell command for this window; the saved PATH is left to the user.
      assert.equal(windowsInstalled.pathCommand, `$env:Path += ';${path.join(root, "bin")}'`);
      const unrunnable = yield* windows(
        installCli().pipe(
          Effect.provideService(
            HostProcess.ExecutablePath,
            path.join(runtimeDir, "versions/100%/t3"),
          ),
        ),
      ).pipe(Effect.flip);
      assert.include(unrunnable.message, "has % or non-ASCII characters");
    }).pipe(Effect.scoped),
  );
});
