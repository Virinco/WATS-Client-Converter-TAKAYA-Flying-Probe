# APT94xx ATD Converter

Converter for TAKAYA APT94xx flying-probe tester ATD result files.

For general instructions on setting up a custom converter in WATS Client, see:
[Setting up a custom converter](https://support.wats.com/hc/en-us/articles/13344321749788-Setting-up-a-custom-converter)

## File Format

The converter handles ATD files produced by TAKAYA APT94xx testers. Key characteristics:

- Date format: `dd/MM/yy HH:mm:ss`
- Step lines use quoted, space-padded fields
- Operator(s) are encoded in the `Serial No.` line, e.g. `Serial No.:21272 - BU0296 (AT21 - OP819)`
- Some files contain two sequential test blocks (BOT + TOP), each producing a separate UUT report

## Converter Arguments

| Argument | Default | Description |
|---|---|---|
| `stationName` | *(none)* | The station/machine name to assign to all reports. When set, this overrides any station name derived from the file. |
| `stationNameFromOperator` | `false` | When `true`, the first token in the `Serial No.` parentheses is used as the station name (e.g. `FP1`, `FP2`, `FP3`) and the second token is used as the UUT operator. See [Operator Handling](#operator-handling). |
| `operationTypeCodeTop` | `30` | Process code used for Top-side (or single-sided) test reports. |
| `operationTypeCodeBottom` | `31` | Process code used for Bottom-side test reports. |
| `GroupByComponentType` | `true` | Controls how test steps are organized (see below). |
| `testModeType` | `Import` | Should remain `Import`. This ensures the tester's own PASS/FAIL judgement is preserved and WATS does not re-evaluate limits. |

### Station Name

The station name is resolved in the following priority order:

1. **`Test ID` field in the file** — present in files produced by newer firmware (e.g. `FP1`, `FP2`, `FP3`). This is the ground truth for which machine ran the test.
2. **First operator token from `Serial No.`** — used when `stationNameFromOperator=true` (see below).
3. **`stationName` converter argument** — fallback for old firmware files that have no `Test ID` line. Note: the WATS Client base class auto-fills this with the submitting PC's machine name, so it only takes effect when neither of the above sources is available.

### Process Codes (Operation Type)

Multi-block files (e.g. AT24 variants) produce two UUT reports — one for each side of the board. The converter uses `operationTypeCodeTop` for the Top side and `operationTypeCodeBottom` for the Bottom side. Single-block files always use `operationTypeCodeTop`.

Set these to match the process codes configured in your WATS system.

### GroupByComponentType

Controls how the test steps are structured in WATS:

- **`true`** (default) — Steps are grouped under sequence calls named by component type prefix (e.g. `C` for capacitors, `R` for resistors, `U` for ICs, `ISH` for insulation/shorts, `IP2P` for point-to-point, `Q` for transistors, `D` for diodes). This makes it easier to browse large reports by component category.

- **`false`** — All steps are added directly under the root sequence call as a flat list, in the order they appear in the file.

## Operator Handling

Operators are parsed from the parenthesized suffix on the `Serial No.` line:

```
Serial No.:21272 - BU0296 (AT21 - OP819)
                           ^^^^   ^^^^^
                           Op 1   Op 2
```

### Default behaviour (`stationNameFromOperator=false`)

- **Operator 1** (e.g. `AT21`) is set as the UUT operator.
- **Operator 2** (e.g. `OP819`), if present, is stored as misc UUT info under the key `Operator 2`.

### When `stationNameFromOperator=true`

Use this when the first token in parentheses is a machine/station identifier (e.g. `FP1`, `FP2`, `FP3`) rather than a human operator:

```
Serial No.:21272 - BU0296 (FP1 - OP819)
                           ^^^   ^^^^^
                         Station  Operator
```

- **Token 1** (e.g. `FP1`) is used as the **station name** (unless overridden by the `stationName` argument).
- **Token 2** (e.g. `OP819`) is set as the **UUT operator**.

## Dual-Measurement (Loop) Steps

Some test steps record two probe measurements — an intermediate attempt and a final decisive measurement. This happens when the tester retries a measurement with a different probe configuration. In the ATD file the step line has three measurement fields instead of two:

```
000545 "U32" "P10-11" "P42998" 532 934 "400.0 O " "  0.0 O  " "409.5 O  " +5% -5% "PASS   " ... "OP"
                                         ^ref       ^probe 1    ^probe 2 (decisive)
```

The converter logs these as **loop steps** in WATS with two iterations:

- **Iteration 0** — the intermediate probe measurement (may be out of limits due to probe contact)
- **Iteration 1** — the final decisive measurement (matches the tester's own PASS/FAIL judgement)

The loop summary shows the total count, how many passed/failed, and the final measurement value.

## Parsed Report Fields

| WATS Field | Source |
|---|---|
| Serial Number | Text after the batch prefix dash, before `(`, e.g. `BU0296` |
| Batch Number | Leading numeric prefix before dash, e.g. `21272` |
| Part Number | Extracted from the `Model:` header line, e.g. `MBEU254288-3` |
| Revision | Software version from model string, e.g. `V2.SWX2` |
| Side | `Top` or `Bottom` (stored as misc UUT info `Side`) |
| Start Date/Time | From the `DATE` header line |
| Operator | First operator token from `Serial No.` line (or second token when `stationNameFromOperator=true`) |
| Station Name | From `stationName` argument, `Test ID` field in file, or first operator token when `stationNameFromOperator=true` |
| Status | PASS/FAIL as reported by the tester |
