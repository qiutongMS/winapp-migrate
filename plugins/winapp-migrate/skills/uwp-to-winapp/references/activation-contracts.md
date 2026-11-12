# Activation routing

Read when the source declares protocol or file activation. Use the report's `activationAnalysis` for declarations and inspection gaps. Mechanical declaration verification does not port the source activation handler or prove payload delivery.

## Preserve the source entry points

Trace each declaration to its source handler, payload, destination page or document, repeated-activation behavior, and error path. These implementation obligations remain even when the resulting UI surface can be reached through ordinary navigation.

## Route desktop activation

Normalize supported payloads at an app-owned routing boundary:

- preserve the initial payload while establishing the app instance and window;
- consume typed AppLifecycle protocol/file data when the target supplies it; parse a full-trust command line only when observed target delivery requires that form;
- preserve source single-instance behavior by redirecting secondary activation without blocking the UI thread or processing the payload twice;
- dispatch to the target navigation or document handler and preserve completion and invalid-payload errors rather than silently treating every activation as a normal launch.

Do not implement speculative delivery paths. When both typed and command-line delivery are required, normalize them before feature logic.

## Hand off activation validation

Keep the activation kind, payload, expected destination, repeat-activation behavior, and error cases with the linked TODO. Payload delivery and instance routing require human or specialized validation through the registered Windows entry point, not just a direct router call. Normal launch, manifest verification, and UI Surface Parity do not establish that result.

An activation declaration marked `review-required` remains a concrete implementation and validation obligation, not a requirement to clear a review counter before UI Surface Parity can pass. Do not mark the source analysis verified merely to remove the handoff. Bind any actual destination surface to its declared surface ID; an activation category is not itself a UI surface.
