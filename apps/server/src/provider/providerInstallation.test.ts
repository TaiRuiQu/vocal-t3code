import * as NodeServices from "@effect/platform-node/NodeServices";
import { assert, describe, it } from "@effect/vitest";
import {
  ProviderDriverKind,
  ProviderInstanceId,
  type ProviderInstallState,
} from "@t3tools/contracts";
import * as HostProcess from "@t3tools/shared/HostProcess";
import * as Effect from "effect/Effect";
import * as FileSystem from "effect/FileSystem";
import * as Layer from "effect/Layer";
import * as Path from "effect/Path";
import * as Stream from "effect/Stream";

import * as ServerSettings from "../serverSettings.ts";
import * as CodexInstallation from "./CodexInstallation.ts";
import type { ProviderInstance } from "@t3tools/provider-core/server/driver";
import * as ProviderInstallation from "./providerInstallation.ts";
import * as ProviderInstanceRegistry from "./ProviderInstanceRegistry.ts";
import * as ProviderRegistry from "./ProviderRegistry.ts";

const instanceId = ProviderInstanceId.make("codex");
const driver = ProviderDriverKind.make("codex");
const state: ProviderInstallState = {
  driver,
  operationId: null,
  phase: "idle",
  downloadedBytes: 0,
  totalBytes: null,
  version: null,
  installedVersion: null,
  canRemove: false,
  message: null,
};

function instance(kind = driver, id = instanceId): ProviderInstance {
  return {
    instanceId: id,
    driverKind: kind,
    enabled: false,
    displayName: undefined,
    continuationIdentity: { driverKind: kind, continuationKey: instanceId },
    get orchestrationAdapter(): never {
      throw new Error("Installation must not start a provider session.");
    },
    get snapshot(): never {
      throw new Error("Installation routing must not probe the provider.");
    },
    get textGeneration(): never {
      throw new Error("Installation must not generate text.");
    },
  };
}

const makeHarness = Effect.fn("providerInstallation.test.makeHarness")(function* (
  input: {
    instance?: ProviderInstance;
    settings?: Parameters<typeof ServerSettings.layerTest>[0];
  } = {},
) {
  const calls: string[] = [];
  let protectedPaths: ReadonlyArray<string> = [];
  const configured = input.instance ?? instance();
  const dependencies = Layer.mergeAll(
    ServerSettings.layerTest({
      ...input.settings,
      providerInstances: {
        [instanceId]: { driver, config: { setupMode: "managed" } },
        ...input.settings?.providerInstances,
      },
    }),
    Layer.mock(ProviderInstanceRegistry.ProviderInstanceRegistry)({
      getInstance: (id) => Effect.succeed(id === configured.instanceId ? configured : undefined),
      listInstances: Effect.succeed([configured]),
    }),
    Layer.mock(ProviderRegistry.ProviderRegistry)({
      refreshInstance: () =>
        Effect.sync(() => {
          calls.push("refresh");
          return [];
        }),
    }),
    Layer.mock(CodexInstallation.CodexInstallation)({
      managedDirectory: "/unused-managed-codex",
      start: Effect.sync(() => {
        calls.push("codex-start");
        return { ...state, driver: ProviderDriverKind.make("codex") };
      }),
      cancel: () =>
        Effect.sync(() => {
          calls.push("codex-cancel");
          return { ...state, driver: ProviderDriverKind.make("codex") };
        }),
      state: Effect.succeed({ ...state, driver: ProviderDriverKind.make("codex") }),
      changes: Stream.succeed({ ...state, driver: ProviderDriverKind.make("codex") }),
      remove: (paths) =>
        Effect.sync(() => {
          protectedPaths = paths ?? [];
          calls.push("codex-remove");
        }),
    }),
  );
  const context = yield* Layer.build(ProviderInstallation.layer.pipe(Layer.provide(dependencies)));
  const connect = Effect.service(ProviderInstallation.ProviderInstallation).pipe(
    Effect.provide(context),
  );
  const router = yield* connect;
  return {
    router,
    connect,
    calls,
    protectedPaths: () => protectedPaths,
  };
});

describe("provider installation routing", () => {
  it.effect("rejects installation for removed providers before running an installer", () =>
    Effect.gen(function* () {
      const harness = yield* makeHarness({
        instance: instance(ProviderDriverKind.make("antigravity")),
      });
      const error = yield* Effect.flip(harness.router.start({ instanceId }));
      assert.include(error.detail, "not available");
      assert.deepEqual(harness.calls, []);
    }).pipe(Effect.scoped, Effect.provide(NodeServices.layer)),
  );

  it.effect("routes managed Codex installation and cancellation", () =>
    Effect.gen(function* () {
      const harness = yield* makeHarness();
      yield* harness.router.start({ instanceId });
      yield* harness.router.cancel({ instanceId, operationId: "install-1" });
      assert.deepEqual(harness.calls, ["codex-start", "codex-cancel"]);
    }).pipe(Effect.scoped, Effect.provide(NodeServices.layer)),
  );

  it.effect("keeps external installs manual without hiding shared install status", () =>
    Effect.gen(function* () {
      const harness = yield* makeHarness({
        settings: {
          providerInstances: {
            [instanceId]: {
              driver,
              config: { setupMode: "managed", binaryPath: "/external/codex" },
            },
          },
        },
      });
      const start = yield* Effect.flip(harness.router.start({ instanceId }));
      const remove = yield* Effect.flip(harness.router.remove({ instanceId }));
      assert.include(start.detail, "custom executable");
      assert.include(remove.detail, "custom executable");
      const observed = yield* Stream.runCollect(harness.router.subscribe({ instanceId }));
      assert.deepEqual(Array.from(observed), [state]);
      assert.deepEqual(harness.calls, []);
    }).pipe(Effect.scoped, Effect.provide(NodeServices.layer)),
  );

  it.effect("protects another instance's binary found through its own PATH", () =>
    Effect.gen(function* () {
      const fs = yield* FileSystem.FileSystem;
      const path = yield* Path.Path;
      const platform = yield* HostProcess.Platform;
      const directory = yield* fs.makeTempDirectoryScoped({ prefix: "t3-provider-install-route-" });
      const binary = platform === "win32" ? "codex-test.exe" : "codex-test";
      const executable = path.join(directory, binary);
      yield* fs.writeFileString(executable, "test");
      yield* fs.chmod(executable, 0o755);
      const other = ProviderInstanceId.make("codex-work");
      const harness = yield* makeHarness({
        settings: {
          providerInstances: {
            [other]: {
              driver,
              config: { binaryPath: binary },
              environment: [{ name: "PATH", value: directory, sensitive: false }],
            },
          },
        },
      });
      yield* harness.router.remove({ instanceId });
      assert.include(harness.protectedPaths(), executable);
      assert.deepEqual(harness.calls, ["codex-remove", "refresh"]);
    }).pipe(Effect.scoped, Effect.provide(NodeServices.layer)),
  );
});
