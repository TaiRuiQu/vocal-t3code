import "vite-plus/test/config";
import { defineConfig } from "vite-plus";
import * as NodeURL from "node:url";
import { remoteCacheUrl } from "./infra/task-cache/src/remoteCache.ts";
import { cachedTestTasks } from "./scripts/lib/cached-tests.ts";

const TEST_EXCLUDE = [
  "**/.repos/**",
  "**/.t3/**",
  "**/node_modules/**",
  "**/dist/**",
  "**/dist-electron/**",
  "**/.{idea,git,cache,output,temp}/**",
];

// Directories whose tests belong to a package's own test config. desktop also
// owns the kde-snap-shot tests through its `include`.
const TEST_CONFIG_PACKAGES = ["apps/server"];

// `vp test run <file>` from the repo root hands each file to its package's
// config. Packages that borrow this config (`--config ../../vite.config.ts`) run
// from their own directory, as CI does, so they keep one plain project.
const isRepoRootTestRun = process.cwd() === import.meta.dirname;

// Vite+ supports remote configuration before its published types include it.
const cacheUrl = remoteCacheUrl(import.meta.dirname);
const taskCacheConfig = {
  scripts: false,
  ...(cacheUrl ? { remote: { url: cacheUrl } } : {}),
};

export default defineConfig({
  run: {
    cache: taskCacheConfig,
    tasks: {
      ...cachedTestTasks(process.env.T3_CACHED_TESTS),
      // The two knip passes read the same files and write nothing, so they run side by side.
      // The `knip:check` script runs this task, so `vp run knip:check` and `pnpm run knip:check` both use it.
      "knip:check:all": {
        command: 'node -e ""',
        dependsOn: ["knip:check:files", "knip:check:exports"],
        cache: false,
      },
    },
  },
  test: {
    environment: "node",
    // Fixture subprocesses must not inherit the outer task runner’s cache policy.
    env: { VP_REMOTE_CACHE: "off", VP_REMOTE_CACHE_URL: "" },
    exclude: TEST_EXCLUDE,
    hookTimeout: 60_000,
    testTimeout: 60_000,
    allowOnly: !process.env.CI,
    // Flaky tests are fixed, not retried.
    retry: 0,
    fakeTimers: {
      toFake: [
        "setTimeout",
        "clearTimeout",
        "setInterval",
        "clearInterval",
        "setImmediate",
        "clearImmediate",
        "Date",
        "performance",
        "requestAnimationFrame",
        "cancelAnimationFrame",
        "requestIdleCallback",
        "cancelIdleCallback",
      ],
    },
    setupFiles: [
      NodeURL.fileURLToPath(
        new URL("./packages/shared/src/testing/longTempDir.ts", import.meta.url),
      ),
      NodeURL.fileURLToPath(
        new URL("./packages/shared/src/testing/providerEnv.ts", import.meta.url),
      ),
    ],
    ...(isRepoRootTestRun
      ? {
          projects: [
            ...TEST_CONFIG_PACKAGES,
            {
              extends: true,
              test: {
                name: "root",
                exclude: [
                  ...TEST_EXCLUDE,
                  "apps/web/**",
                  "apps/desktop/**",
                  "apps/mobile/**",
                  "apps/marketing/**",
                  "packages/client-runtime/**",
                  ...TEST_CONFIG_PACKAGES.map((dir) => `${dir}/**`),
                  "native/kde-snap-shot/**",
                ],
              },
            },
          ],
        }
      : {}),
  },
  staged: {
    // In order, so lint reads the formatted file. CI lint failures cost a full round trip.
    "*": ["vp fmt --no-error-on-unmatched-pattern", "vp lint --no-error-on-unmatched-pattern"],
    // knip checks the whole repo, so it takes no file list. Any code or manifest change can leave
    // a file, export, or dependency unused.
    "{*.{js,jsx,ts,tsx,mjs,cjs,mts,cts},package.json,knip.jsonc}": () => "vp run knip:check",
  },
  fmt: {
    ignorePatterns: [
      ".repos/**",
      "infra/task-cache/vendor/**",
      // Macroscope requires one glob per line; Markdown formatting corrupts it.
      ".macroscope/ignore.md",
      ".alchemy",
      "dist",
      "dist-electron",
      "node_modules",
      "pnpm-lock.yaml",
      "*.tsbuildinfo",
      "**/routeTree.gen.ts",
      "**/*.graphql.ts",
      "packages/source-control-github/schema/**",
      "apps/server/schema/**",
      "apps/mobile/android/**",
      "apps/mobile/ios/**",
      "apps/mobile/uniwind-types.d.ts",
      // Vendored and generated C.
      "apps/mobile/modules/t3-highlights/Vendor/**",
      "apps/mobile/modules/t3-highlights/cpp/generated/**",
      "apps/mobile/modules/t3-highlights/nitrogen/generated/**",
      "*.icon/**",
    ],
    sortPackageJson: {},
    overrides: [
      {
        files: [".devcontainer/devcontainer.json"],
        options: {
          trailingComma: "none",
        },
      },
    ],
  },
  lint: {
    ignorePatterns: [
      ".repos",
      ".repos/**",
      "infra/task-cache/vendor/**",
      "dist",
      "dist-electron",
      "node_modules",
      "pnpm-lock.yaml",
      "*.tsbuildinfo",
      "**/routeTree.gen.ts",
      "**/*.graphql.ts",
      "packages/source-control-github/schema/**",
      "apps/server/schema/**",
      "apps/mobile/android/**",
      "apps/mobile/ios/**",
      "apps/mobile/uniwind-types.d.ts",
    ],
    plugins: ["eslint", "oxc", "unicorn", "typescript"],
    jsPlugins: ["./oxlint-plugin-t3code/index.ts"],
    categories: {
      correctness: "warn",
      suspicious: "warn",
      perf: "warn",
    },
    rules: {
      "oxc/no-accumulating-spread": "error",
      "eslint/no-control-regex": "off",
      "typescript/no-this-alias": "off",
      "unicorn/prefer-add-event-listener": "off",
      "unicorn/prefer-string-starts-ends-with": "off",
      "unicorn/prefer-set-has": "off",
      "unicorn/prefer-array-find": "off",
      "unicorn/no-new-array": "off",
      "unicorn/no-array-reverse": "off",
      "typescript/no-extraneous-class": "error",
      "eslint/no-empty-pattern": "error",
      "eslint/no-new": "error",
      "unicorn/no-useless-spread": "error",
      "eslint/no-unused-vars": [
        "error",
        { argsIgnorePattern: "^_", varsIgnorePattern: "^_", caughtErrorsIgnorePattern: "^_" },
      ],
      "eslint/no-unsafe-optional-chaining": "error",
      "eslint/no-unneeded-ternary": "error",
      "unicorn/no-useless-fallback-in-spread": "error",
      "unicorn/require-module-specifiers": "error",
      "unicorn/no-array-sort": "off",
      "unicorn/consistent-function-scoping": "off",
      "oxc/no-map-spread": "off",
      "eslint/no-shadow": "off",
      "eslint/no-await-in-loop": "off",
      "eslint/no-underscore-dangle": "off",
      "typescript/consistent-return": "off",
      "typescript/no-base-to-string": "off",
      "typescript/no-duplicate-type-constituents": "off",
      "typescript/no-floating-promises": "off",
      "typescript/no-implied-eval": "off",
      "typescript/no-meaningless-void-operator": "off",
      "typescript/no-redundant-type-constituents": "off",
      "typescript/no-unnecessary-boolean-literal-compare": "off",
      "typescript/no-unnecessary-type-conversion": "off",
      "typescript/no-unnecessary-type-arguments": "off",
      "typescript/no-unnecessary-type-assertion": "off",
      "typescript/no-unnecessary-type-parameters": "off",
      "typescript/no-unsafe-type-assertion": "off",
      "typescript/await-thenable": "off",
      "typescript/require-array-sort-compare": "off",
      "typescript/restrict-template-expressions": "off",
      "typescript/unbound-method": "off",
      "t3code/no-global-process-runtime": "error",
      "t3code/no-inline-schema-compile": "error",
      "t3code/no-low-value-comments": "error",
      "t3code/no-manual-effect-runtime-in-tests": "error",
      "t3code/no-native-title-tooltip": "error",
      // Test files only (the rule checks the filename).
      "t3code/no-polling-loop-in-tests": "error",
      "t3code/no-raw-mcp-registration": "error",
      "t3code/no-real-time-wait-in-tests": "error",
      "t3code/no-test-in-loop": "error",
      "t3code/no-test-timeout-or-unreasoned-skip": "error",
      "t3code/no-rpc-permission-bypass": ["error", { allowRawClientAccess: true }],
      "t3code/no-unscoped-has": "error",
      "t3code/no-wall-clock-in-tests": "error",
      "t3code/namespace-node-imports": "error",
      "t3code/prefer-catch-tags": "error",
      "t3code/prefer-effect-fn": "error",
      "t3code/require-centered-scroll-gutter": "error",
      "t3code/require-suppression-reason": "error",
    },
    overrides: [
      {
        files: [
          "**/*.{test,spec,testkit}.{ts,tsx}",
          "**/{test,tests,testkit,testing,fixtures,integration}/**",
          "packages/provider-testing/**",
        ],
        rules: { "t3code/prefer-effect-fn": "off" },
      },
      {
        files: ["apps/**/src/**", "packages/**/src/**"],
        rules: { "t3code/no-dom-test-environment": "error" },
      },
      {
        files: ["**/*.{test,spec}.{ts,tsx,js,jsx,mts,cts,mjs,cjs}"],
        rules: { "t3code/no-rpc-permission-bypass": "off" },
      },
      {
        files: ["packages/shared/src/HostProcess.ts"],
        rules: { "t3code/no-global-process-runtime": "off" },
      },
      {
        // The `t3` npm launcher is published as plain, dependency-free Node.
        files: ["scripts/npm-launcher/**"],
        rules: {
          "t3code/no-global-process-runtime": "off",
          "t3code/namespace-node-imports": "off",
        },
      },
      {
        files: ["apps/server/src/mcp/McpHttpServer.ts"],
        rules: { "t3code/no-raw-mcp-registration": "off" },
      },
      {
        // The server and every client load a forge's shared definition, so a UI runtime
        // imported there would break the server; client-only forge code goes in `src/client/`.
        files: ["packages/source-control-*/src/shared/**"],
        rules: {
          "eslint/no-restricted-imports": [
            "error",
            {
              patterns: [
                {
                  group: [
                    "react",
                    "react/*",
                    "react-*",
                    "expo",
                    "expo-*",
                    "lucide-react",
                    "@t3tools/client-runtime",
                    "@t3tools/client-runtime/*",
                    "../client/*",
                    "../server/*",
                    "../../client/*",
                    "../../server/*",
                    "../../../client/*",
                    "../../../server/*",
                    "@t3tools/source-control-*/client/*",
                    "@t3tools/source-control-*/server/*",
                  ],
                  message:
                    "Shared forge definitions load on the server and every client; keep UI and server code out.",
                },
              ],
            },
          ],
        },
      },
      {
        files: ["apps/server/src/**", "packages/source-control-*/src/server/**"],
        rules: {
          "eslint/no-restricted-imports": [
            "error",
            {
              patterns: [
                {
                  group: [
                    "@t3tools/source-control-*/client/*",
                    "../client/*",
                    "../../client/*",
                    "../../../client/*",
                  ],
                  message:
                    "A forge's client entry is UI-only; the server reads its `shared/definition`.",
                },
              ],
            },
          ],
        },
      },
      {
        files: ["apps/*/src/**", "packages/*/src/**"],
        excludeFiles: [
          "**/*.{test,spec}.{ts,tsx,js,jsx,mts,cts,mjs,cjs}",
          "**/testkit/**",
          "**/_generated/**",
          // Real user quotes stay verbatim.
          "apps/marketing/src/lib/tweets.ts",
        ],
        rules: { "t3code/no-em-dash-copy": "error" },
      },
      {
        // ESNext types allow APIs absent from Hermes; restrict shared client code, exempting Node tests.
        files: ["packages/contracts/src/**", "packages/shared/src/**"],
        excludeFiles: ["**/*.test.ts", "**/*.test.tsx"],
        rules: { "t3code/no-hermes-unsupported-apis": "error" },
      },
      {
        files: ["packages/*/src/**"],
        rules: { "t3code/no-unreasoned-state-sync": "error" },
      },
    ],
    options: {
      reportUnusedDisableDirectives: "error",
      typeAware: false,
      typeCheck: false,
    },
  },
});
