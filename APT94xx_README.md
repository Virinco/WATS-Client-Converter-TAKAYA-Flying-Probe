# APT94xx ATD Converter

Converter for TAKAYA APT94xx flying-probe tester ATD result files.

For general instructions on setting up a custom converter in WATS Client, see:
[Setting up a custom converter](https://support.wats.com/hc/en-us/articles/13344321749788-Setting-up-a-custom-converter)

## File Format

The converter handles ATD files produced by TAKAYA APT94xx testers. Key characteristics:

- Date format: `yy/MM/dd HH:mm:ss`
- Step lines use quoted, space-padded fields
- Operator(s) are encoded in the `Serial No.` line, e.g. `Serial No.:21272 - BU0296 (AT21 - OP819)`
- Some files contain two sequential test blocks (BOT + TOP), each producing a separate UUT report

## Converter Arguments

| Argument | Default | Description |
|---|---|---|
| `stationName` | *(none)* | **Required.** The station/machine name to assign to all reports. This must be configured in the WATS Client converter setup since the ATD files do not contain a station name. |
| `operationTypeCodeTop` | `30` | Process code used for Top-side (or single-sided) test reports. |
| `operationTypeCodeBottom` | `31` | Process code used for Bottom-side test reports. |
| `GroupByComponentType` | `true` | Controls how test steps are organized (see below). |
| `testModeType` | `Import` | Should remain `Import`. This ensures the tester's own PASS/FAIL judgement is preserved and WATS does not re-evaluate limits. |

### Station Name

The ATD files do not contain a station or machine identifier. You **must** set the `stationName` argument in the WATS Client converter configuration to identify the tester. If left blank, no station name will be assigned to the reports.

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

- **Operator 1** (e.g. `AT21`) is set as the UUT operator.
- **Operator 2** (e.g. `OP819`), if present, is stored as misc UUT info under the key `Operator 2`.

## Parsed Report Fields

| WATS Field | Source |
|---|---|
| Serial Number | Text after the batch prefix dash, before `(`, e.g. `BU0296` |
| Batch Number | Leading numeric prefix before dash, e.g. `21272` |
| Part Number | Extracted from the `Model:` header line, e.g. `MBEU254288-3` |
| Revision | Software version from model string, e.g. `V2.SWX2` |
| Side | `Top` or `Bottom` (stored as misc UUT info `Side`) |
| Start Date/Time | From the `DATE` header line |
| Operator | First operator token from `Serial No.` line |
| Station Name | From `stationName` converter argument |
| Status | PASS/FAIL as reported by the tester |
