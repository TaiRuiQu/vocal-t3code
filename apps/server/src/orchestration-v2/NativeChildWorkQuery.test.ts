import { expect, it } from "@effect/vitest";
import { ThreadId } from "@t3tools/contracts";
import * as Effect from "effect/Effect";
import * as Layer from "effect/Layer";

import * as NativeChildWorkQuery from "./NativeChildWorkQuery.ts";
import * as ThreadManagement from "./ThreadManagementService.ts";

it.effect("returns no out-of-band child work for a retained historical thread", () =>
  Effect.gen(function* () {
    const query = yield* NativeChildWorkQuery.NativeChildWorkQuery;
    expect(
      yield* query.getNativeChildWork({ threadId: ThreadId.make("historical-thread") }),
    ).toEqual({ children: [] });
  }).pipe(
    Effect.provide(
      NativeChildWorkQuery.layer.pipe(
        Layer.provide(
          Layer.mock(ThreadManagement.ThreadManagementService)({
            getThreadRecords: () => Effect.succeed({} as never),
          }),
        ),
      ),
    ),
  ),
);
