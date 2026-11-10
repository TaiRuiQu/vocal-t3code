export const STAGE_INSTALL_ARGS = ["install", "--prod"] as const;

type Platform = "mac" | "linux" | "win";
type Arch = "arm64" | "x64" | "universal";

export function resolveFffNativeDependencies(
  platform: Platform,
  arch: Arch,
  version: string,
): Record<string, string> {
  const architectures = arch === "universal" ? (["arm64", "x64"] as const) : [arch];
  const target = platform === "mac" ? "darwin" : platform === "win" ? "win32" : "linux";
  return Object.fromEntries(
    architectures.flatMap((architecture) =>
      (platform === "linux" ? ["-gnu", "-musl"] : [""]).map((libc) => [
        `@ff-labs/fff-bin-${target}-${architecture}${libc}`,
        version,
      ]),
    ),
  );
}

export function createStageWorkspaceConfig(input: {
  readonly platform: Platform;
  readonly arch: Arch;
  readonly allowBuilds?: Record<string, boolean>;
  readonly patchedDependencies?: Record<string, string>;
  readonly overrides?: Record<string, string>;
}) {
  const { platform, arch, allowBuilds, patchedDependencies, overrides } = input;
  return {
    supportedArchitectures: {
      os: [platform === "mac" ? "darwin" : platform === "win" ? "win32" : "linux"],
      cpu: arch === "universal" ? ["arm64", "x64"] : [arch],
      ...(platform === "linux" ? { libc: ["glibc"] } : {}),
    },
    ...(allowBuilds && Object.keys(allowBuilds).length > 0 ? { allowBuilds } : {}),
    ...(patchedDependencies && Object.keys(patchedDependencies).length > 0
      ? { patchedDependencies }
      : {}),
    ...(overrides && Object.keys(overrides).length > 0 ? { overrides } : {}),
  };
}

export function createStagePatchedDependencies(
  patchedDependencies: Record<string, string>,
  dependencies: Record<string, unknown>,
): Record<string, string> {
  return Object.fromEntries(
    Object.entries(patchedDependencies).filter(([patchKey]) => {
      const versionSeparator = patchKey.lastIndexOf("@");
      return Object.hasOwn(
        dependencies,
        versionSeparator > 0 ? patchKey.slice(0, versionSeparator) : patchKey,
      );
    }),
  );
}
