import {
  OrchestrationV2GetNativeChildWorkError,
  type OrchestrationV2GetNativeChildWorkInput,
  type OrchestrationV2GetNativeChildWorkResult,
} from "@t3tools/contracts";
import * as Context from "effect/Context";
import * as Effect from "effect/Effect";
import * as Layer from "effect/Layer";

import * as ThreadManagement from "./ThreadManagementService.ts";

export class NativeChildWorkQuery extends Context.Service<
  NativeChildWorkQuery,
  {
    readonly getNativeChildWork: (
      input: OrchestrationV2GetNativeChildWorkInput,
    ) => Effect.Effect<
      OrchestrationV2GetNativeChildWorkResult,
      OrchestrationV2GetNativeChildWorkError
    >;
  }
>()("t3/orchestration-v2/NativeChildWorkQuery") {}

const make = Effect.gen(function* () {
  const threads = yield* ThreadManagement.ThreadManagementService;
  const getNativeChildWork = Effect.fn("NativeChildWorkQuery.getNativeChildWork")(
    (input: OrchestrationV2GetNativeChildWorkInput) =>
      threads.getThreadRecords(input.threadId, []).pipe(
        Effect.as<OrchestrationV2GetNativeChildWorkResult>({ children: [] }),
        Effect.mapError(
          (cause) =>
            new OrchestrationV2GetNativeChildWorkError({ threadId: input.threadId, cause }),
        ),
      ),
  );
  return NativeChildWorkQuery.of({ getNativeChildWork });
});

export const layer = Layer.effect(NativeChildWorkQuery, make);
