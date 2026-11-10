import * as Schema from "effect/Schema";

export class ServerCliCommandExitError extends Schema.TaggedError<ServerCliCommandExitError>()(
  "ServerCliCommandExitError",
  {
    command: Schema.String,
    args: Schema.Array(Schema.String),
    cwd: Schema.optional(Schema.String),
    exitCode: Schema.Int,
  },
) {
  override get message(): string {
    return `Command exited with non-zero exit code (${this.exitCode})`;
  }
}

export class ServerCliBuildAssetMissingError extends Schema.TaggedError<ServerCliBuildAssetMissingError>()(
  "ServerCliBuildAssetMissingError",
  {
    assetPath: Schema.String,
  },
) {
  override get message(): string {
    return `Missing build asset: ${this.assetPath}. Run the build subcommand first.`;
  }
}

export class ServerCliExecutableImportError extends Schema.TaggedError<ServerCliExecutableImportError>()(
  "ServerCliExecutableImportError",
  {
    bundlePath: Schema.String,
    specifiers: Schema.Array(Schema.String),
  },
) {
  override get message(): string {
    return `${this.bundlePath} imports file-backed packages that a single-executable cannot resolve: ${this.specifiers.join(", ")}. Load them through createRequire instead.`;
  }
}

export class ServerCliBootstrapTooLargeError extends Schema.TaggedError<ServerCliBootstrapTooLargeError>()(
  "ServerCliBootstrapTooLargeError",
  {
    bootstrapPath: Schema.String,
    bytes: Schema.Number,
  },
) {
  override get message(): string {
    return `${this.bootstrapPath} is ${this.bytes} bytes. The CLI was bundled into the executable bootstrap; keep ./binMain.ts external.`;
  }
}
