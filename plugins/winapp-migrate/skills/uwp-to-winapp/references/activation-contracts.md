# Activation contracts

Use this protocol for every protocol or file-association contract in schema `1.5` `activationAnalysis`. `winapp-migrate uwp` owns source declaration inventory, safe target manifest translation, and declaration verification. The target owns activation routing and behavior; the schema `1.0` state plan owns runtime proof.

## 1. Separate declaration coverage from behavior

Resolve every `review-required`, `incomplete`, or `failed` activation-analysis issue in the active feature scope before claiming the manifest contract complete. A mechanically verified declaration proves only that the eligible target declaration matches source facts. It does not prove process routing, single-instance behavior, navigation, file loading, state restoration, or error handling.

Trace the source declaration to its activation entry point and observable outcome. Record the activation kind, payload, required process/window state, navigation or document action, repeated-activation behavior, and user-visible failure path in the state plan.

## 2. Establish one target-owned routing boundary

Normalize every supported activation into one app-owned router before it reaches feature code:

1. establish the main app instance and window ownership without losing the initial activation;
2. use typed AppLifecycle protocol or file data when packaged desktop activation supplies it;
3. when observed platform delivery uses a full-trust command line, parse and normalize that form once at the same boundary rather than adding feature-specific command-line handling;
4. redirect a secondary activation to the main instance when the source contract is single-instance, without blocking the UI thread or applying the payload twice;
5. dispatch the normalized payload to the existing target navigation, document, or service contract and await app-owned work whose completion governs the observable outcome;
6. surface invalid or unsupported payloads through the source-equivalent error path instead of silently falling back to a normal launch.

Do not implement typed and command-line paths speculatively. Retain only paths established by target platform evidence, and make both converge before business logic when both are required.

## 3. Replay the operating-system contract

Build and register the packaged target before replay. For each activation state:

1. invoke the registered protocol or file association through the corresponding Windows launcher mechanism, not by calling the router directly;
2. confirm the intended payload reached the expected page, document, state, or visible error;
3. repeat activation while the main instance is running and verify source-equivalent instance and window behavior;
4. verify navigation, selection, document content, and back or close restoration rather than process survival alone;
5. retain the launch result, process/window inventory, UI tree or non-visual outcome, and health evidence under the state ID.

Resolve the linked activation TODO only after every required contract has a verified mechanical declaration, implemented routing, and successful target replay. Keep behavioral parity partial or unverified when the source activation cannot provide usable comparison evidence.
