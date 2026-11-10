#!/usr/bin/env node

import * as NodeOS from "node:os";

import * as NodeRuntime from "@effect/platform-node/NodeRuntime";
import * as NodeServices from "@effect/platform-node/NodeServices";
import * as NetService from "@t3tools/shared/Net";
import { resolveGitWorktreePath, resolveWorktreeT3Home } from "@t3tools/shared/devHome";
import * as HostProcess from "@t3tools/shared/HostProcess";
import { resolveSpawnCommand } from "@t3tools/shared/shell";
import * as Config from "effect/Config";
import * as Effect from "effect/Effect";
import * as FileSystem from "effect/FileSystem";
import * as Hash from "effect/Hash";
import * as Layer from "effect/Layer";
import * as Logger from "effect/Logger";
import * as Option from "effect/Option";
import * as Path from "effect/Path";
import * as Schema from "effect/Schema";
import { Argument, Command, Flag } from "effect/cli";
import { ChildProcess } from "effect/process";

import { loadRepoEnv } from "./lib/public-config.ts";

Object.assign(process.env, loadRepoEnv());

const BASE_SERVER_PORT = 13773;
const MAX_HASH_OFFSET = 3000;
const MAX_PORT = 65535;
const LARGE_DEV_DATABASE_BYTES = 500 * 1024 * 1024;
const DEV_PORT_PROBE_HOSTS = ["127.0.0.1", "::1"] as const;
const DEV_RUNNER_MODES = ["dev", "dev:server"] as const;
type DevMode = (typeof DEV_RUNNER_MODES)[number];
type PortAvailabilityCheck<R = never> = (port: number) => Effect.Effect<boolean, never, R>;

export const DEFAULT_T3_HOME = Effect.map(Effect.service(Path.Path), (path) =>
  path.join(NodeOS.homedir(), ".t3"),
);

export class DevRunnerConfigurationError extends Schema.TaggedError<DevRunnerConfigurationError>()(
  "DevRunnerConfigurationError",
  {
    configKeys: Schema.Array(Schema.String),
    cause: Schema.Defect(),
  },
) {
  override get message(): string {
    return `Failed to read dev-runner configuration: ${this.configKeys.join(", ")}.`;
  }
}

export class DevRunnerInvalidPortOffsetError extends Schema.TaggedError<DevRunnerInvalidPortOffsetError>()(
  "DevRunnerInvalidPortOffsetError",
  {
    configKey: Schema.Literal("T3CODE_PORT_OFFSET"),
    portOffset: Schema.Number,
    minimum: Schema.Number,
  },
) {
  override get message(): string {
    return `${this.configKey} must be at least ${this.minimum}; received ${this.portOffset}.`;
  }
}

export class DevRunnerPortExhaustedError extends Schema.TaggedError<DevRunnerPortExhaustedError>()(
  "DevRunnerPortExhaustedError",
  {
    startOffset: Schema.Number,
    baseServerPort: Schema.Number,
    maximumPort: Schema.Number,
  },
) {
  override get message(): string {
    return `No dev server port was available from offset ${this.startOffset} through maximum port ${this.maximumPort}.`;
  }
}

export class DevRunnerProcessError extends Schema.TaggedError<DevRunnerProcessError>()(
  "DevRunnerProcessError",
  {
    operation: Schema.Literals(["spawn", "wait-for-exit"]),
    mode: Schema.Literals(DEV_RUNNER_MODES),
    executable: Schema.Literal("vp"),
    argumentCount: Schema.Number,
    shell: Schema.Boolean,
    cause: Schema.Defect(),
  },
) {
  override get message(): string {
    return `Dev-runner process operation "${this.operation}" failed for mode "${this.mode}".`;
  }
}

export class DevRunnerProcessExitError extends Schema.TaggedError<DevRunnerProcessExitError>()(
  "DevRunnerProcessExitError",
  {
    mode: Schema.Literals(DEV_RUNNER_MODES),
    executable: Schema.Literal("vp"),
    argumentCount: Schema.Number,
    shell: Schema.Boolean,
    exitCode: Schema.Number,
  },
) {
  override get message(): string {
    return `Dev-runner process exited with code ${this.exitCode} in mode "${this.mode}".`;
  }
}

const optionalStringConfig = (name: string): Config.Config<string | undefined> =>
  Config.String(name).pipe(
    Config.option,
    Config.map((value) => Option.getOrUndefined(value)),
  );
const optionalBooleanConfig = (name: string): Config.Config<boolean | undefined> =>
  Config.Boolean(name).pipe(
    Config.option,
    Config.map((value) => Option.getOrUndefined(value)),
  );
const optionalPortConfig = (name: string): Config.Config<number | undefined> =>
  Config.Port(name).pipe(
    Config.option,
    Config.map((value) => Option.getOrUndefined(value)),
  );
const optionalIntegerConfig = (name: string): Config.Config<number | undefined> =>
  Config.Int(name).pipe(
    Config.option,
    Config.map((value) => Option.getOrUndefined(value)),
  );
const OffsetConfig = Config.all({
  portOffset: optionalIntegerConfig("T3CODE_PORT_OFFSET"),
  devInstance: optionalStringConfig("T3CODE_DEV_INSTANCE"),
});

export function resolveOffset(config: {
  readonly portOffset: number | undefined;
  readonly devInstance: string | undefined;
  readonly worktreePath?: string | undefined;
}): Effect.Effect<
  { readonly offset: number; readonly source: string },
  DevRunnerInvalidPortOffsetError
> {
  if (config.portOffset !== undefined) {
    if (config.portOffset < 0) {
      return Effect.fail(
        new DevRunnerInvalidPortOffsetError({
          configKey: "T3CODE_PORT_OFFSET",
          portOffset: config.portOffset,
          minimum: 0,
        }),
      );
    }
    return Effect.succeed({
      offset: config.portOffset,
      source: `T3CODE_PORT_OFFSET=${config.portOffset}`,
    });
  }

  const seed = config.devInstance?.trim();
  if (seed) {
    if (/^\d+$/.test(seed)) {
      return Effect.succeed({
        offset: Number(seed),
        source: `numeric T3CODE_DEV_INSTANCE=${seed}`,
      });
    }

    const offset = ((Hash.string(seed) >>> 0) % MAX_HASH_OFFSET) + 1;
    return Effect.succeed({ offset, source: `hashed T3CODE_DEV_INSTANCE=${seed}` });
  }

  const worktreePath = config.worktreePath?.trim();
  if (worktreePath) {
    const offset = ((Hash.string(worktreePath) >>> 0) % MAX_HASH_OFFSET) + 1;
    return Effect.succeed({ offset, source: `worktree ${worktreePath}` });
  }

  return Effect.succeed({ offset: 0, source: "default ports" });
}

const resolveBaseDir = Effect.fn("dev-runner.resolveBaseDir")(function* (
  baseDir: string | undefined,
): Effect.fn.Return<string, never, Path.Path> {
  const path = yield* Path.Path;
  const configured = baseDir?.trim();

  if (configured === "~") {
    return yield* HostProcess.HomeDirectory;
  }
  if (configured?.startsWith("~/") || configured?.startsWith("~\\")) {
    return path.join(yield* HostProcess.HomeDirectory, configured.slice(2));
  }
  if (configured) {
    return path.resolve(configured);
  }

  return yield* DEFAULT_T3_HOME;
});

interface CreateDevRunnerEnvInput {
  readonly baseEnv: NodeJS.ProcessEnv;
  readonly serverOffset: number;
  readonly t3Home: string | undefined;
  readonly autoBootstrapProjectFromCwd: boolean | undefined;
  readonly logWebSocketEvents: boolean | undefined;
  readonly host: string | undefined;
  readonly port: number | undefined;
}

export const createDevRunnerEnv = Effect.fn("dev-runner.createDevRunnerEnv")(function* (
  input: CreateDevRunnerEnvInput,
): Effect.fn.Return<NodeJS.ProcessEnv, never, Path.Path> {
  const output: NodeJS.ProcessEnv = {
    ...input.baseEnv,
    T3CODE_PORT: String(input.port ?? BASE_SERVER_PORT + input.serverOffset),
    T3CODE_MODE: "web",
    T3CODE_NO_BROWSER: "1",
    T3CODE_TELEMETRY_ENABLED: input.baseEnv.T3CODE_TELEMETRY_ENABLED ?? "false",
  };
  for (const key of [
    "PORT",
    "HOST",
    "VITE_DEV_SERVER_URL",
    "VITE_HTTP_URL",
    "VITE_WS_URL",
    "T3CODE_SINGLE_ORIGIN_DEV",
    "T3CODE_DESKTOP_WS_URL",
    "T3_SERVICE_LAUNCHER_CONTEXT",
    "T3_BOOT_SERVICE_UNIT",
  ])
    delete output[key];
  if (input.t3Home?.trim()) output.T3CODE_HOME = yield* resolveBaseDir(input.t3Home);
  else delete output.T3CODE_HOME;
  if (input.host !== undefined) output.T3CODE_HOST = input.host;
  if (input.autoBootstrapProjectFromCwd !== undefined) {
    output.T3CODE_AUTO_BOOTSTRAP_PROJECT_FROM_CWD = input.autoBootstrapProjectFromCwd ? "1" : "0";
  } else delete output.T3CODE_AUTO_BOOTSTRAP_PROJECT_FROM_CWD;
  if (input.logWebSocketEvents !== undefined)
    output.T3CODE_LOG_WS_EVENTS = input.logWebSocketEvents ? "1" : "0";
  else delete output.T3CODE_LOG_WS_EVENTS;
  return output;
});

export const checkPortAvailabilityOnHosts = Effect.fn("dev-runner.checkPortAvailabilityOnHosts")(
  function* <R>(
    port: number,
    hosts: ReadonlyArray<string>,
    canListenOnHost: (port: number, host: string) => Effect.Effect<boolean, never, R>,
  ): Effect.fn.Return<boolean, never, R> {
    for (const host of hosts) {
      if (!(yield* canListenOnHost(port, host))) {
        return false;
      }
    }

    return true;
  },
);

export function devPortProbeHosts(configuredHost: string | undefined): ReadonlyArray<string> {
  const host = configuredHost?.trim();
  if (!host || DEV_PORT_PROBE_HOSTS.includes(host as (typeof DEV_PORT_PROBE_HOSTS)[number])) {
    return DEV_PORT_PROBE_HOSTS;
  }
  return [...DEV_PORT_PROBE_HOSTS, host];
}

const makeDefaultCheckPortAvailability =
  (configuredHost: string | undefined): PortAvailabilityCheck<NetService.NetService> =>
  (port) =>
    Effect.gen(function* () {
      const net = yield* NetService.NetService;
      return yield* checkPortAvailabilityOnHosts(
        port,
        devPortProbeHosts(configuredHost),
        (candidate, host) => net.canListenOnHost(candidate, host),
      );
    });

export const findFirstAvailableOffset = Effect.fn("dev-runner.findFirstAvailableOffset")(function* <
  R = NetService.NetService,
>(input: {
  readonly startOffset: number;
  readonly checkPortAvailability: PortAvailabilityCheck<R>;
}): Effect.fn.Return<number, DevRunnerPortExhaustedError, R> {
  for (
    let candidate = input.startOffset;
    BASE_SERVER_PORT + candidate <= MAX_PORT;
    candidate += 1
  ) {
    if (yield* input.checkPortAvailability(BASE_SERVER_PORT + candidate)) return candidate;
  }
  return yield* new DevRunnerPortExhaustedError({
    startOffset: input.startOffset,
    baseServerPort: BASE_SERVER_PORT,
    maximumPort: MAX_PORT,
  });
});

/** Best effort: a failed first-start step warns, and dev still starts. */
const runRepoScript = Effect.fn("dev-runner.runRepoScript")(function* (
  script: string,
  args: ReadonlyArray<string>,
  env: NodeJS.ProcessEnv,
) {
  const path = yield* Path.Path;
  const repoRoot = path.dirname(import.meta.dirname);
  const exitCode = yield* ChildProcess.make(
    yield* HostProcess.ExecutablePath,
    [path.join(repoRoot, script), ...args],
    { cwd: repoRoot, stdin: "ignore", stdout: "inherit", stderr: "inherit", env, extendEnv: false },
  ).pipe(
    Effect.flatMap((child) => child.exitCode),
    Effect.scoped,
    Effect.orElseSucceed(() => -1),
  );
  if (exitCode !== 0) {
    yield* Effect.logWarning(`[dev-runner] ${script} failed; starting anyway.`);
  }
});

/**
 * The first start in a worktree seeds its empty database from a small live database
 * only while it has no database. Later starts keep their existing data.
 */
const prepareFirstStart = Effect.fn("dev-runner.prepareFirstStart")(function* (input: {
  readonly seed: boolean;
  readonly worktreeHome: string | undefined;
  readonly env: NodeJS.ProcessEnv;
}) {
  const fs = yield* FileSystem.FileSystem;
  const path = yield* Path.Path;
  const exists = (file: string) => fs.exists(file).pipe(Effect.orElseSucceed(() => false));

  if (input.worktreeHome !== undefined) {
    const stateDir = path.join(input.worktreeHome, "userdata");
    for (const name of ["statev2.sqlite", "state.sqlite"]) {
      const file = path.join(stateDir, name);
      const info = yield* fs.stat(file).pipe(Effect.option);
      if (Option.isSome(info) && Number(info.value.size) > LARGE_DEV_DATABASE_BYTES) {
        yield* Effect.logWarning(
          `[dev-runner] ${file} is ${Math.round(Number(info.value.size) / 1024 / 1024)} MB. With the dev server stopped, \`vp run migrate-dev-db\` rebuilds statev2.sqlite as a small copy of recent data.`,
        );
      }
    }
    const source = path.join(yield* HostProcess.HomeDirectory, ".t3", "userdata", "statev2.sqlite");
    // A V1 state.sqlite counts as data too: the server imports it only while
    // statev2.sqlite is missing, so seeding would hide it.
    if (
      input.seed &&
      !(yield* exists(path.join(stateDir, "statev2.sqlite"))) &&
      !(yield* exists(path.join(stateDir, "state.sqlite")))
    ) {
      const info = yield* fs.stat(source).pipe(Effect.option);
      if (Option.isSome(info)) {
        if (Number(info.value.size) > LARGE_DEV_DATABASE_BYTES) {
          yield* Effect.logWarning(
            `[dev-runner] ${source} exceeds 500 MiB; skipping automatic database seeding and starting with an empty database. Run \`vp run migrate-dev-db\` manually with dev stopped if needed; it may use substantial disk space before pruning.`,
          );
        } else {
          yield* Effect.logInfo(
            `[dev-runner] seeding ${input.worktreeHome} from ${source} (read-only). --no-seed or T3CODE_DEV_SEED=0 skips this.`,
          );
          yield* runRepoScript(
            "apps/server/scripts/migrate-dev-db.ts",
            ["--base-dir", input.worktreeHome, "--source", source],
            input.env,
          );
        }
      }
    }
  }
});

interface DevRunnerCliInput {
  readonly mode: DevMode;
  readonly t3Home: string | undefined;
  readonly autoBootstrapProjectFromCwd: boolean | undefined;
  readonly logWebSocketEvents: boolean | undefined;
  readonly host: string | undefined;
  readonly port: number | undefined;
  readonly dryRun: boolean;
  readonly seed?: boolean | undefined;
  readonly runArgs: ReadonlyArray<string>;
}

export const runDevRunnerWithInput = Effect.fn("dev-runner.runDevRunnerWithInput")(function* (
  input: DevRunnerCliInput,
) {
  const { portOffset, devInstance } = yield* OffsetConfig.pipe(
    Effect.mapError(
      (cause) =>
        new DevRunnerConfigurationError({
          configKeys: ["T3CODE_PORT_OFFSET", "T3CODE_DEV_INSTANCE"],
          cause,
        }),
    ),
  );

  const worktreePath = yield* resolveGitWorktreePath(yield* HostProcess.WorkingDirectory);

  const { offset, source } = yield* resolveOffset({
    portOffset,
    devInstance,
    worktreePath,
  });

  const serverOffset =
    input.port === undefined
      ? yield* findFirstAvailableOffset({
          startOffset: offset,
          checkPortAvailability: makeDefaultCheckPortAvailability(input.host),
        })
      : offset;

  const hostEnvironment = yield* HostProcess.Environment;
  const path = yield* Path.Path;
  // Every checkout owns its dev data; an ambient home may be the live install.
  const worktreeHome =
    (yield* resolveWorktreeT3Home(yield* HostProcess.WorkingDirectory)) ??
    path.resolve(import.meta.dirname, "../.t3");
  // Trim before choosing: `--home-dir ""` is not a selection, and treating it
  // as one would skip the worktree default and land on the shared home —
  // exactly the outcome this precedence exists to prevent.
  const resolvedT3Home = (input.t3Home?.trim() || undefined) ?? worktreeHome;
  const env = yield* createDevRunnerEnv({
    baseEnv: hostEnvironment,
    serverOffset,
    t3Home: resolvedT3Home,
    autoBootstrapProjectFromCwd: input.autoBootstrapProjectFromCwd,
    logWebSocketEvents: input.logWebSocketEvents,
    host: input.host,
    port: input.port,
  });

  const baseDir = env.T3CODE_HOME ?? (yield* DEFAULT_T3_HOME);
  yield* Effect.logInfo(
    `[dev-runner] mode=${input.mode} source=${source} serverPort=${String(env.T3CODE_PORT)} baseDir=${baseDir}`,
  );
  if (input.dryRun) return;
  yield* prepareFirstStart({
    seed: input.seed !== false,
    worktreeHome: baseDir === worktreeHome ? worktreeHome : undefined,
    env,
  });
  const args = ["run", "--filter=t3", "dev", ...input.runArgs];
  const spawnCommand = yield* resolveSpawnCommand("vp", args, { env });
  const processContext = {
    mode: input.mode,
    executable: "vp" as const,
    argumentCount: args.length,
    shell: spawnCommand.shell,
  } as const;
  const child = yield* ChildProcess.make(spawnCommand.command, spawnCommand.args, {
    stdin: "inherit",
    stdout: "inherit",
    stderr: "inherit",
    env,
    extendEnv: false,
    shell: spawnCommand.shell,
    // Keep Vite+ in the same process group so terminal signals (Ctrl+C)
    // reach it directly. Effect defaults to detached: true on non-Windows,
    // which would put the runner in a new group and require manual forwarding.
    detached: false,
    forceKillAfter: "1500 millis",
  }).pipe(
    Effect.mapError(
      (cause) =>
        new DevRunnerProcessError({
          ...processContext,
          operation: "spawn",
          cause,
        }),
    ),
  );

  const exitCode = yield* child.exitCode.pipe(
    Effect.mapError(
      (cause) =>
        new DevRunnerProcessError({
          ...processContext,
          operation: "wait-for-exit",
          cause,
        }),
    ),
  );
  if (exitCode !== 0) {
    return yield* new DevRunnerProcessExitError({
      ...processContext,
      exitCode,
    });
  }
});

const devRunnerCli = Command.make("dev-runner", {
  mode: Argument.Literals("mode", DEV_RUNNER_MODES).pipe(
    Argument.withDescription("Development mode to run."),
  ),
  t3Home: Flag.String("home-dir").pipe(
    Flag.withDescription(
      "Explicit development data directory. Defaults to this checkout's .t3; ambient T3CODE_HOME is ignored.",
    ),
    Flag.optional,
    Flag.map(Option.getOrUndefined),
  ),
  autoBootstrapProjectFromCwd: Flag.Boolean("auto-bootstrap-project-from-cwd").pipe(
    Flag.withDescription(
      "Auto-bootstrap toggle (equivalent to T3CODE_AUTO_BOOTSTRAP_PROJECT_FROM_CWD).",
    ),
    Flag.withFallbackConfig(optionalBooleanConfig("T3CODE_AUTO_BOOTSTRAP_PROJECT_FROM_CWD")),
  ),
  logWebSocketEvents: Flag.Boolean("log-websocket-events").pipe(
    Flag.withDescription("WebSocket event logging toggle (equivalent to T3CODE_LOG_WS_EVENTS)."),
    Flag.withAlias("log-ws-events"),
    Flag.withFallbackConfig(optionalBooleanConfig("T3CODE_LOG_WS_EVENTS")),
  ),
  host: Flag.String("host").pipe(
    Flag.withDescription("Server host/interface override (forwards to T3CODE_HOST)."),
    Flag.withFallbackConfig(optionalStringConfig("T3CODE_HOST")),
  ),
  port: Flag.Int("port").pipe(
    Flag.withSchema(Schema.Int.check(Schema.isBetween({ minimum: 1, maximum: 65535 }))),
    Flag.withDescription("Server port override (forwards to T3CODE_PORT)."),
    Flag.withFallbackConfig(optionalPortConfig("T3CODE_PORT")),
  ),
  dryRun: Flag.Boolean("dry-run").pipe(
    Flag.withDescription("Resolve mode/ports/env and print, but do not spawn Vite+."),
    Flag.withDefault(false),
  ),
  seed: Flag.Boolean("seed").pipe(
    Flag.withDescription(
      "Seed an empty worktree database from ~/.t3 on first start (read-only, via migrate-dev-db) if the source is at most 500 MiB. On by default; --no-seed or T3CODE_DEV_SEED=0 skips it.",
    ),
    Flag.withFallbackConfig(optionalBooleanConfig("T3CODE_DEV_SEED")),
  ),
  runArgs: Argument.String("run-arg").pipe(
    Argument.withDescription("Additional Vite+ run args (pass after `--`)."),
    Argument.variadic(),
  ),
}).pipe(
  Command.withDescription(
    "Run the terminal host with isolated development data and a deterministic port.",
  ),
  Command.withHandler((input) => runDevRunnerWithInput(input)),
);

const cliRuntimeLayer = Layer.mergeAll(
  Logger.layer([Logger.consolePretty()]),
  NodeServices.layer,
  NetService.layer,
);

if (import.meta.main) {
  Command.run(devRunnerCli, { version: "0.0.0" }).pipe(
    Effect.scoped,
    Effect.provide(cliRuntimeLayer),
    NodeRuntime.runMain,
  );
}
