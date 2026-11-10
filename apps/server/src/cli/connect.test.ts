// @effect-diagnostics nodeBuiltinImport:off - CLI integration exercises the Node HTTP boundary.
import * as NodeHttp from "node:http";

import * as NodeCrypto from "@effect/platform-node/NodeCrypto";
import * as NodeServices from "@effect/platform-node/NodeServices";
import * as NetService from "@t3tools/shared/Net";
import * as RelayClient from "@t3tools/shared/relayClient";
import { assert, it } from "@effect/vitest";
import * as ConfigProvider from "effect/ConfigProvider";
import * as Console from "effect/Console";
import * as Duration from "effect/Duration";
import * as Effect from "effect/Effect";
import * as Fiber from "effect/Fiber";
import * as FileSystem from "effect/FileSystem";
import * as Layer from "effect/Layer";
import * as Option from "effect/Option";
import * as TestConsole from "effect/testing/TestConsole";
import * as TestClock from "effect/testing/TestClock";
import { Command } from "effect/cli";
import { HttpClient, HttpClientResponse } from "effect/http";

import * as ServerSecretStore from "../auth/ServerSecretStore.ts";
import { makeCli } from "../binCli.ts";
import * as CliState from "../cloud/CliState.ts";
import * as CliTokenManager from "../cloud/CliTokenManager.ts";
import * as ExternalLauncher from "../process/externalLauncher.ts";
import {
  makePersistedServerRuntimeState,
  persistServerRuntimeState,
} from "../serverRuntimeState.ts";
import { acquireRelayClientForLink, linkEnvironmentForConnect } from "./connect.ts";

const managedExecutable = {
  status: "available",
  executablePath: "/tmp/cloudflared",
  source: "managed",
  version: RelayClient.CLOUDFLARED_VERSION,
} as const;

it.effect("does not install the relay client when the user declines the managed download", () =>
  Effect.gen(function* () {
    let installCalls = 0;
    const result = yield* acquireRelayClientForLink(
      {
        resolve: Effect.succeed({
          status: "missing",
          version: RelayClient.CLOUDFLARED_VERSION,
        }),
        install: Effect.sync(() => {
          installCalls += 1;
          return managedExecutable;
        }),
        installWithProgress: () =>
          Effect.sync(() => {
            installCalls += 1;
            return managedExecutable;
          }),
        pruneManagedVersions: Effect.void,
      },
      () => Effect.succeed(false),
      () => Effect.void,
    );

    assert.isTrue(Option.isNone(result));
    assert.equal(installCalls, 0);
  }),
);

it.effect.each(["saved", "missing"] as const)(
  "enables a managed connection with a %s credential before checking the relay client",
  (credential) =>
    Effect.gen(function* () {
      const operations: string[] = [];
      const secrets = new Map<string, Uint8Array>();
      if (credential === "saved") {
        secrets.set(
          "cloud-cli-oauth-token",
          new TextEncoder().encode(
            JSON.stringify({
              accessToken: "saved-access",
              refreshToken: "saved-refresh",
              expiresAtEpochMs: 3_600_000,
            }),
          ),
        );
      }
      const externalDependencies = Layer.mergeAll(
        NodeServices.layer,
        NodeCrypto.layer,
        Layer.mock(ServerSecretStore.ServerSecretStore)({
          get: (name) => Effect.sync(() => Option.fromUndefinedOr(secrets.get(name))),
          set: (name, value) =>
            Effect.sync(() => {
              operations.push(`save:${name}`);
              secrets.set(name, value);
            }),
        }),
        Layer.mock(ExternalLauncher.ExternalLauncher)({
          launchBrowser: () => Effect.die("Terminal sign-in must not open a browser"),
        }),
        Layer.succeed(
          HttpClient.HttpClient,
          HttpClient.make((request) =>
            Effect.sync(() => {
              operations.push(request.url);
              return HttpClientResponse.fromWeb(
                request,
                Response.json(
                  request.url.endsWith("/oauth/device_authorization")
                    ? {
                        device_code: "device-code",
                        user_code: "BCDF-GHJK",
                        verification_uri: "https://accounts.example.test/device",
                        expires_in: 600,
                        interval: 5,
                      }
                    : {
                        access_token: "new-access",
                        refresh_token: "new-refresh",
                        expires_in: 3600,
                        token_type: "Bearer",
                      },
                ),
              );
            }),
          ),
        ),
        ConfigProvider.layer(
          ConfigProvider.fromEnv({
            env: {
              T3CODE_CLERK_PUBLISHABLE_KEY: "pk_test_Y2xlcmsuZXhhbXBsZS50ZXN0JA==",
              T3CODE_CLERK_CLI_OAUTH_CLIENT_ID: "oauth-client-test",
            },
          }),
        ),
        Layer.mock(RelayClient.RelayClient)({
          resolve: Effect.sync(() => {
            operations.push("relay");
            return managedExecutable;
          }),
        }),
      );
      yield* Effect.gen(function* () {
        const linking = yield* linkEnvironmentForConnect({
          headless: true,
          installRelayClient: true,
        }).pipe(Effect.forkChild);
        yield* TestClock.adjust(Duration.seconds(5));
        assert.isNotNull(yield* Fiber.join(linking));
        assert.equal(yield* CliState.readCliDesiredLinkMode, "managed");
        assert.isTrue(yield* CliState.readCliDesiredCloudLink);
        const manager = yield* CliTokenManager.CloudCliTokenManager;
        const stored = Option.getOrThrow(yield* manager.getExisting);
        assert.equal(stored.accessToken, credential === "saved" ? "saved-access" : "new-access");
        assert.equal(stored.refreshToken, credential === "saved" ? "saved-refresh" : "new-refresh");
        const output = (yield* TestConsole.logLines).join("\n");
        if (credential === "saved") {
          assert.deepEqual(operations, ["relay", "save:cloud-cli-desired-link"]);
          assert.notInclude(output, "Sign in");
        } else {
          assert.deepEqual(operations, [
            "https://clerk.example.test/oauth/device_authorization",
            "https://clerk.example.test/oauth/token",
            "save:cloud-cli-oauth-token",
            "relay",
            "save:cloud-cli-desired-link",
          ]);
          assert.include(output, "https://accounts.example.test/device");
          assert.include(output, "BCDF-GHJK");
        }
        assert.notInclude(output, "new-access");
        assert.notInclude(output, "saved-access");
      }).pipe(
        Effect.provide(
          CliTokenManager.layer.pipe(
            Layer.provideMerge(
              Layer.mergeAll(externalDependencies, Layer.fresh(TestConsole.layer)),
            ),
          ),
        ),
      );
    }),
);

it.layer(Layer.mergeAll(NodeServices.layer, NodeCrypto.layer, NetService.layer))(
  "t3 connect unlink",
  (it) => {
    const runUnlink = (recordedPid: number, options?: { readonly portFreed?: boolean }) =>
      Effect.gen(function* () {
        const requests: Array<string> = [];
        const warnings: Array<string> = [];
        const server = yield* Effect.acquireRelease(
          Effect.callback<NodeHttp.Server>((resume) => {
            const listening = NodeHttp.createServer((request, response) => {
              requests.push(`${request.method} ${request.url}`);
              response.writeHead(200, { "content-type": "application/json" });
              response.end(JSON.stringify({ ok: true, endpointRuntimeStatus: null }));
            });
            listening.listen(0, "127.0.0.1", () => resume(Effect.succeed(listening)));
          }),
          (listening) => Effect.sync(() => listening.close()),
        );
        const address = server.address();
        if (address === null || typeof address === "string") {
          return yield* Effect.die(new Error("Expected a TCP address"));
        }
        if (options?.portFreed) {
          yield* Effect.callback<void>((resume) => {
            server.close(() => resume(Effect.void));
          });
        }

        const fs = yield* FileSystem.FileSystem;
        const baseDir = yield* fs.makeTempDirectoryScoped({ prefix: "t3-connect-unlink-test-" });
        const state = yield* makePersistedServerRuntimeState({
          config: { host: "127.0.0.1", devUrl: undefined },
          port: address.port,
        });
        yield* persistServerRuntimeState({
          path: `${baseDir}/userdata/server-runtime.json`,
          state: { ...state, pid: recordedPid },
        });

        yield* Command.runWith(makeCli({ cloudEnabled: true }), { version: "0.0.0" })([
          "connect",
          "unlink",
          "--base-dir",
          baseDir,
        ]).pipe(
          // TestConsole has no reader for Console.warn.
          Effect.updateService(Console.Console, (console) => ({
            ...console,
            warn: (...args: ReadonlyArray<unknown>) => {
              warnings.push(args.join(" "));
            },
          })),
        );

        return { requests, warnings, printed: (yield* TestConsole.logLines).join("\n") };
      }).pipe(
        Effect.scoped,
        Effect.provide(
          Layer.mergeAll(
            ConfigProvider.layer(ConfigProvider.fromEnv({ env: {} })),
            Layer.fresh(TestConsole.layer),
          ),
        ),
      );

    // Above Linux's PID ceiling, as in pair.test.ts: no process can hold it.
    const deadPid = 4_194_305;

    it.effect("does not tell the user to restart a server that is gone", () =>
      Effect.gen(function* () {
        const { printed, warnings } = yield* runUnlink(deadPid, { portFreed: true });

        assert.include(printed, "T3 Connect is disabled locally.");
        assert.notInclude(warnings.join("\n"), "Restart that server");
      }),
    );

    it.effect("warns that a server that is gone may have left its connector running", () =>
      Effect.gen(function* () {
        const { warnings } = yield* runUnlink(deadPid, { portFreed: true });

        assert.include(warnings.join("\n"), "its cloudflared process may still be running");
      }),
    );

    it.effect("does not send a token to whatever took over a dead server's port", () =>
      Effect.gen(function* () {
        const { requests } = yield* runUnlink(deadPid);

        assert.deepEqual(requests, []);
      }),
    );

    it.effect("asks a running server to stop its tunnel", () =>
      Effect.gen(function* () {
        const { requests, printed, warnings } = yield* runUnlink(process.pid);

        assert.deepEqual(requests, ["POST /api/connect/unlink"]);
        assert.include(printed, "T3 Connect is disabled locally.");
        assert.notInclude(warnings.join("\n"), "cloudflared process");
      }),
    );
  },
);
