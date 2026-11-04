# Skill execution contracts

`guard-skill-execution.ps1` is a contract-driven `preToolUse` policy engine.
It is independent of the UWP migration state machine and can be reused by
another Skill by supplying a different schema `1.0` contract.

A contract declares:

- how to recognize its active workspace from a marker and JSON requirements;
- shell executable and verb combinations that must be denied;
- opaque or dynamic process launchers that cannot be inspected safely;
- optional file-write rules for contracts that need a protected workspace.

The UWP policy is
`skills/uwp-to-winapp/skill-execution-contract.json`. Plugin `hooks.json`
passes that file to the generic guard. The guard persists workspace identity
per contract and session so changing the shell working directory cannot bypass
the boundary.

This layer constrains observable tool side effects; it does not prove that a
model read or understood Skill prose. Outcome correctness remains the
migration closure's responsibility. A Skill that requires stronger isolation
must also remove unrestricted mutation capabilities or run workers in a
sandbox where only issued workspaces are writable.
