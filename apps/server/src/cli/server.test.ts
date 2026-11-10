import * as NodeCrypto from "@effect/platform-node/NodeCrypto";
import * as NodeServices from "@effect/platform-node/NodeServices";
import { assert, it } from "@effect/vitest";
import * as NetService from "@t3tools/shared/Net";
import * as ConfigProvider from "effect/ConfigProvider";
import * as Effect from "effect/Effect";
import * as FileSystem from "effect/FileSystem";
import * as Layer from "effect/Layer";
import { Command } from "effect/cli";
import { vi } from "vite-plus/test";

import { makeCli } from "../binCli.ts";
import type * as ServerConfig from "../config.ts";

const startup = vi.hoisted(() => ({
  configs: [] as ServerConfig.ServerConfig["Service"][],
  operations: [] as string[],
}));

vi.mock("../server.ts", async () => {
  const ServerConfig = await import("../config.ts");
  return {
    runServer: Effect.gen(function* () {
      startup.configs.push(yield* ServerConfig.ServerConfig);
      startup.operations.push("serve");
    }),
  };
});

vi.mock("../cloud/publicConfig.ts", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../cloud/publicConfig.ts")>();
  return { ...actual, hasCloudPublicConfig: true };
});

vi.mock("./connect.ts", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./connect.ts")>();
  return {
    ...actual,
    runCloudCommand: (_flags: unknown, run: Effect.Effect<unknown>) => run,
    requireConnectAllowed: Effect.void,
    linkEnvironmentForConnect: () =>
      Effect.sync(() => {
        startup.operations.push("authorize");
        return { identity: "fixture@example.test" };
      }),
  };
});

it.layer(Layer.mergeAll(NodeServices.layer, NodeCrypto.layer, NetService.layer))(
  "terminal host startup",
  (it) => {
    it.effect.each(["default", "start", "serve"] as const)(
      "%s runs headlessly and only manual launch authorizes T3 Connect",
      (command) =>
        Effect.gen(function* () {
          startup.configs.length = 0;
          startup.operations.length = 0;
          const fs = yield* FileSystem.FileSystem;
          const baseDir = yield* fs.makeTempDirectoryScoped({ prefix: "t3-terminal-startup-" });
          yield* Command.runWith(makeCli(), { version: "0.0.0" })([
            ...(command === "default" ? [] : [command]),
            "--base-dir",
            baseDir,
            "--port",
            "3773",
          ]).pipe(
            Effect.provide(
              ConfigProvider.layer(
                ConfigProvider.fromEnv({
                  env: {
                    T3CODE_NO_BROWSER: "false",
                    T3CODE_AUTO_BOOTSTRAP_PROJECT_FROM_CWD: "true",
                  },
                }),
              ),
            ),
          );
          assert.deepEqual(
            startup.operations,
            command === "serve" ? ["serve"] : ["authorize", "serve"],
          );
          assert.lengthOf(startup.configs, 1);
          const config = startup.configs[0]!;
          assert.equal(config.baseDir, baseDir);
          assert.equal(config.startupPresentation, "headless");
          assert.isTrue(config.noBrowser);
          assert.isFalse(config.autoBootstrapProjectFromCwd);
        }),
    );
  },
);
