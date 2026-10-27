# Contributing Guide

Thanks for contributing to SolidPilot. This document summarizes how to set up the development environment, the architectural rules, and the flow for adding a new capability.

SolidPilot is an AI-driven CAD automation system for SolidWorks that runs as an MCP server. For an overview, see [README.md](README.md).

---

## Architecture and invariants

The system has four layers, and preserving these boundaries is the foundation of the project:

| Layer | Directory | Responsibility |
|---|---|---|
| `cad-planner` | `cad-planner/` | Intent -> CAD-neutral Feature Graph IR. Does not touch COM, does not emit raw tool calls. |
| `compiler/solidworks` | `compiler/solidworks/` | IR -> tool calls + reference resolution. Deterministic; contains no LLM and no MCP. |
| `execution/solidworks` | `execution/solidworks/` | The only layer that touches SolidWorks COM (C#, .NET Framework). Runs **inside SolidWorks** as the SolidPilot Bridge add-in (`SolidPilotBridge/`, default); the same tool source also builds as a standalone exe (`SolidworksExecution/`, fallback). |
| `adapters/claude` | `adapters/claude/` | MCP protocol bridge (Python, MCP SDK v2). |

Rules that must never be violated:

- The AI works at the **feature level**; only the compiler knows tools; only `SolidWorksService.cs` touches COM.
- **MCP is the top boundary** (between the client and the IR), not an internal transport. The compiler reaches the execution layer over plain REST.
- The IR is **CAD-neutral**; SolidWorks-specific details live only in the compiler and execution layers.
- The execution and planner layers have no knowledge of which client is calling them.
- **One tool source, two hosts.** `SolidworksExecution/Services/*` and `Models/*` are compiled into the standalone exe **and** linked (not copied) into the add-in's `SolidPilotBridge.Tools` assembly. Tool logic is never host-specific; only the thin host around it differs (REST controller + STA executor in the exe, `ToolsHost` + `ToolGate` in the add-in). The two hosts serve the same REST contract.
- The add-in **shell** (`SolidPilotBridge.dll`) is BCL-only and deliberately small: SolidWorks loads it into its own AppDomain, so third-party packages clash, and every shell change needs a SolidWorks restart. Put new behaviour in the **tools** assembly, which hot-reloads.
- Adding a new CAD backend means adding a new compiler and execution layer; the IR and `cad-planner` stay unchanged.
- The adapter layer is provider-specific. Supporting a new AI client (OpenClaw, OpenAI, a local LLM, etc.) means only adding a new adapter that reuses the shared bridge core.

---

## Development environment

### Requirements
- Windows and **SolidWorks 2026** (development happens against a local SolidWorks install).
- **.NET Framework 4.8** and MSBuild (ships with Visual Studio 2022).
- **Python 3.x** and the official **MCP Python SDK v2** (`mcp>=2.0.0`) for the MCP adapter.

### Building and running the execution layer

**In-process (default)** — the tools run inside SolidWorks as the SolidPilot Bridge add-in. One-time setup (SolidWorks closed; see the README's *Installation* for the details):

```
powershell -NoProfile -ExecutionPolicy Bypass -File execution\solidworks\SolidPilotBridge\deploy-bridge.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File execution\solidworks\SolidPilotBridge\register-bridge.ps1 -Hive HKLM     # elevated, once
```

The everyday loop for a tool change needs **no SolidWorks restart**:

```
powershell -File execution\solidworks\SolidPilotBridge\deploy-bridge.ps1 -ToolsOnly            # rebuild the tools assembly
powershell -File execution\solidworks\SolidPilotBridge\bridge-ping.ps1 -Command reload_tools   # swap it in (generation +1; state_version and idempotency kept)
powershell -File execution\solidworks\SolidPilotBridge\tests\tool-call.ps1 feature_data '{"action":"list"}'   # call any tool by hand
```

To explore an API question before writing a tool, use the in-process script runner: `bridge-ping.ps1 -Command "run_cs <file.cs>"` (or `probes\run-probe.ps1 <file.cs> [arg]`) compiles a C# snippet inside SolidWorks and runs it against the live API. Add-in diagnostics are in `%LOCALAPPDATA%\SolidPilot\bridge.log`; the safety net's snapshots and the in-flight marker are in the same folder.

**Standalone exe (fallback)** — the original out-of-process host. Build:

```
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" execution\solidworks\SolidworksExecution.sln /t:Build /p:Configuration=Debug
```

Restart (headless, `http://localhost:5000`; use `SOLIDWORKS_EXEC_PORT` for another port when the add-in is running):

```
Get-Process SolidworksExecution -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Process execution\solidworks\SolidworksExecution\bin\Debug\SolidworksExecution.exe -WindowStyle Hidden
```

The exe runs headless and must be running while SolidWorks is open (the COM connection is established lazily, on the first tool call), and the adapter must be started with `EXECUTION_MODE=exe`. C#-side errors and per-request traces are written to `execution\solidworks\SolidworksExecution\bin\Debug\execution.log`. Never commit the Debug binaries a local build modifies under `SolidworksExecution/bin/Debug`.

### Running the adapter

```
cd adapters/claude
pip install -r requirements.txt
python server.py
```

**Important:** the Python MCP adapter does not hot-reload while running. When you change `server.py` (for example, adding a new parameter), the MCP server must be reconnected. Reconnecting also resets the adapter's local `state_version` to 0. (The C# execution server, by contrast, can be restarted.)

---

## Adding a new capability

### A new low-level tool (execution surface)

1. **Contract** — add the tool to `execution/solidworks/contracts/tool-schemas.json`.
2. **Execution** — add a `case "tool_name":` in `Services/ToolDispatcher.cs` (shared by both hosts) and implement it in the matching `SolidWorksService.<Family>.cs` **partial class** under `Services/` (one class split by tool family: core / Sketch / Features / SheetMetal / Drawing / Analyze / Assembly; a new file is linked into the add-in's tools assembly automatically, but needs a `<Compile Include>` entry in `SolidworksExecution.csproj` for the exe fallback). **Verify any SolidWorks COM API signature by reflecting the interop assembly first — never invent a method name or argument list.** The real API frequently differs from what looks plausible (for example, the model-item insertion API lives on `IDrawingDoc`, not `IView`; `GetLines3` returns empty while `GetPolylines7` is the working geometry getter). Decode unknown return shapes empirically against a live document before writing the parser.
3. **Adapter** — register the MCP tool in `adapters/claude/server.py`. Model-facing guidance lives here (the client never sees `tool-schemas.json`): use `Literal` enums for discriminators, Pydantic `Field` constraints for units and ranges, and real required parameters.
4. **Verify** — run the contract test (see below), hot-reload the tools (`deploy-bridge.ps1 -ToolsOnly`, `bridge-ping.ps1 -Command reload_tools`) and validate against live SolidWorks. A tool is done when a case in a live suite (`execution/solidworks/SolidPilotBridge/tests`) asserts its *result* — geometry, numbers, the refusals — and not merely `COMPLETED`: `ModifyDefinition` reports success for values SolidWorks ignored, and sketch inference (on by default) silently snapped a rectangle's corners to nearby model edges. Test a sketch- or selection-based tool in a part that **already has geometry**, not only in an empty one.

### A new feature (IR level — the strategic direction)

1. Add the feature type to `cad-planner/contracts/feature-graph.schema.json` (this also registers the capability).
2. Add its lowering rule and any required reference resolution in `compiler/solidworks`.
3. It reuses existing low-level tools; usually no new execution tool is needed.

> **The two IR doors:** `rebuild_from_ir` is the **reverse** door (rebuilds a part/assembly from its analysis artifact's IR block — the round-trip that verifies an LLM-proposed IR). `submit_feature_graph` is the **forward** door (builds from a Feature Graph the model supplies directly, from design intent, with no original to copy); it is **live and gate-free**. Both doors run through the same `pycompiler` — **never fork the compiler**. Note the different failure surface: the reverse door can warn `source_stale` because it has an artifact hash to check, whereas the forward door takes a raw graph and has nothing to compare against — so a forward run must self-verify by computing its expected outcome (see the `recipe://usage/forward` resource).

---

## Conventions

- **Units:** all lengths are in meters (SolidWorks internal units). Angles are taken in degrees at the adapter boundary.
- **HTTP semantics:** all CAD results return HTTP 200; `FAILED` and `DUPLICATE` are domain states, not transport errors. HTTP 400 is only for malformed requests or unknown tool names.
- **state_version:** every request is checked with strict equality; a mismatch returns `FAILED` with `INVALID_STATE_VERSION`. The adapter increments its local value only on a `COMPLETED` response.
- **Variable-length array parameters** must be passed as a JSON string at the MCP boundary (e.g. `points: str = "[]"`), not as a `list`/`List` type. The MCP client stringifies list-typed arguments, which makes a list-typed parameter uncallable; parse the string in the adapter (`json.loads`) or in C#.
- **C# JSON:** parameters are deserialized as a `JObject`; use `p.Value<T>("key")`. Serialization is camelCase and null values are omitted.
- **swconst enums:** must be used as inlined constants, never as runtime types (the relevant DLL is not copied into `bin/Debug`; using one as a type causes a runtime load failure).

---

## Testing

- **Contract test:** catches any tool or parameter drift between `server.py` and `tool-schemas.json`, **and guards the MCP resource surface**: every `recipe://` / `schema://` URI mentioned anywhere in `server.py` must resolve to a registered resource or template, every served recipe section must exist in `recipe-usage.md`, and a section present in the file but NOT served fails too (so the surface cannot grow by accident either). That second half matters because the recipe is reached by URI now — a renamed URI whose docstring pointer was not updated would otherwise fail *silently*, with the model simply never finding the rules.

  ```
  cd adapters/claude
  python tests/test_schema_contract.py
  ```

  or run it via `pytest`.

- **Live testing:** tools are verified against live SolidWorks, with the GUI open. The in-process host made this automatable: the PowerShell suites under `execution/solidworks/SolidPilotBridge/tests` (bridge, stress, soak, sheet-metal typed-control / forming / parametric, an end-to-end adapter round trip) run against the add-in on `localhost:5000` and need SolidWorks running with nothing else open — the README's *Live test suites* table says what each one covers. Drawing tools are still checked case by case. A cohesive batch of tools is chosen together and tested as a batch. When a tool fails, inspect `execution.log` and report the expected result, the API response, and a hypothesis. For drawing tools, the exported **PDF is the ground truth** — some interop counters under-report (e.g. inserted annotations / center marks), so confirm a drawing change by exporting and reading the PDF rather than trusting an in-band count alone.

- **Compiler suite:** `compiler/solidworks/pycompiler/tests/` exercises IR validation, lowering, and reference resolution against a fake execution port — fully offline.

  ```
  cd compiler/solidworks
  python -m pycompiler.tests.test_compiler
  ```

Both offline suites run in **CI on every push/PR** (`.github/workflows/ci.yml`). The C# layer is not built in CI — it requires the licensed SolidWorks interop assemblies; C# behavior is verified live instead.

---

## Contributor License Agreement (CLA)

SolidPilot is free and open source under the [GNU AGPL-3.0](LICENSE), while the
project owner also offers a separate **commercial license** for organizations
that cannot comply with the AGPL. For this dual-licensing model to work, every
contribution must come with a clear grant of rights. The full terms live in
**[CLA.md](CLA.md)**; the summary below restates them. **By submitting a
contribution (a pull request, patch, or any other work) you agree to the
following:**

1. **You have the right to contribute it.** The contribution is your original work,
   or you have the necessary rights to submit it, and submitting it does not violate
   any third party's rights or any agreement you are bound by.
2. **License and commercial-use grant.** You grant the project owner a perpetual,
   worldwide, non-exclusive, royalty-free, irrevocable license to use, reproduce,
   modify, distribute, sublicense, and **relicense** your contribution — including
   for **commercial purposes** and as part of a paid or cloud edition — and to
   include it in the project under the current license or any future license the
   owner chooses.
3. **You keep your copyright.** This is a license grant, not an assignment; you
   retain ownership of your contribution and may use it elsewhere.
4. **License adjustment.** You agree that the owner may make the project's license
   more or less permissive as needed, and that your contribution may be distributed
   under such adjusted terms.
5. **As-is.** You provide your contribution without warranty of any kind.

**How to accept:** sign off your commits with `git commit -s` (adds a
`Signed-off-by` line, per [CLA.md](CLA.md)), or include the following line in
your pull request description:

> I have read and agree to the Contributor License Agreement in CLA.md.

> **Note on significant contributions.** For small fixes, the pull-request
> acknowledgment above is sufficient. For a substantial contribution, the owner may
> ask you to also confirm the CLA via a signed document (a wet signature or a secure
> electronic signature) — this makes the grant robust under copyright law.
> This is not a legal opinion; it is a practical safeguard.

---

## Commits and Pull Requests

- Write meaningful, focused commits; do not mix several unrelated changes in one commit.
- If you change the behavior of a tool or feature, keep the relevant contract and tests up to date.
- In the Pull Request description, state what you changed, why, and how you verified it.
- Include the CLA acknowledgment line (see [Contributor License Agreement](#contributor-license-agreement-cla) above).

For questions and discussions, feel free to open an issue.
