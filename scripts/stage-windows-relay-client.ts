import * as NodeCrypto from "@effect/platform-node/NodeCrypto";
import * as NodeRuntime from "@effect/platform-node/NodeRuntime";
import * as NodeServices from "@effect/platform-node/NodeServices";
import * as HostProcess from "@t3tools/shared/HostProcess";
import * as RelayClient from "@t3tools/shared/relayClient";
import * as ConfigProvider from "effect/ConfigProvider";
import * as Effect from "effect/Effect";
import * as FileSystem from "effect/FileSystem";
import * as Layer from "effect/Layer";
import * as Path from "effect/Path";
import { FetchHttpClient, HttpClient, HttpClientResponse } from "effect/http";

Effect.gen(function* () {
  if ((yield* HostProcess.Platform) !== "win32" || (yield* HostProcess.Architecture) !== "x64") {
    return yield* Effect.die(new Error("Stage the Windows relay client on a Windows x64 host."));
  }
  const fs = yield* FileSystem.FileSystem;
  const path = yield* Path.Path;
  const baseDir = yield* fs.makeTempDirectoryScoped({ prefix: "t3-bundle-relay-" });
  const client = yield* RelayClient.makeCloudflaredRelayClient({ baseDir });
  const installed = yield* client.install;
  const destination = path.resolve(import.meta.dirname, "../apps/server/resources/relay-client");
  yield* fs.makeDirectory(destination, { recursive: true });
  yield* fs.copyFile(installed.executablePath, path.join(destination, "cloudflared.exe"));

  const http = yield* HttpClient.HttpClient;
  const license = yield* http
    .get(
      `https://raw.githubusercontent.com/cloudflare/cloudflared/${RelayClient.CLOUDFLARED_VERSION}/LICENSE`,
    )
    .pipe(
      Effect.flatMap(HttpClientResponse.filterStatusOk),
      Effect.flatMap((response) => response.text),
    );
  yield* fs.writeFileString(path.join(destination, "LICENSE"), license);
  yield* Effect.log(`Staged cloudflared ${installed.version} for Windows x64.`);
}).pipe(
  Effect.scoped,
  Effect.provide(
    Layer.mergeAll(
      NodeServices.layer,
      NodeCrypto.layer,
      FetchHttpClient.layer,
      ConfigProvider.layer(ConfigProvider.fromEnv({ env: {} })),
    ),
  ),
  NodeRuntime.runMain,
);
