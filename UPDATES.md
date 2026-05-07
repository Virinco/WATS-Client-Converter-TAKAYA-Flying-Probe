# APT94xx ATD Converter – Update Notes

## New example set: 22160-BV series (AT33 / AT34), added May 2026

Ninety-six new ATD files produced by customer batch `22160` were added to
`Examples/APT94xx_ATD/`. They are split across two test programs:

| Suffix | Side    | Test program | Files      |
|--------|---------|--------------|------------|
| AT33   | Bottom  | Single-side  | BV0001–BV0040 |
| AT34   | Top     | Single-side  | BV0041–BV0096 |

---

### Deviation 1 — `Test ID` field carries station name  
**Status: already handled**

All 22160 files contain a `Test ID :FP3` line in the block header, e.g.:

```
Test ID :FP3
```

`FP3` is the station/machine name. The converter captures this via the `TestID`
search field (added in the March 2026 update) and assigns it to `StationName`.
Priority order: explicit `stationName` converter argument → `Test ID` from file →
operator token (when `stationNameFromOperator=true`).

---

### Deviation 2 — Model string contains `_PANEL_` infix  
**Status: already handled**

The model field uses the pattern `MBEU284209-5B_PANEL_V1.SWX2` (bottom) and
`MBEU284209-5T_PANEL_V1.SWX2` (top), which includes a literal `_PANEL_` segment
between the board part number and the software version.

`ParseModel` already strips any leading `PANEL` or `SINGLE` segment from the
right-hand side of the first underscore via a case-insensitive regex, so this
parses correctly:

| Field           | Value           |
|-----------------|-----------------|
| Part number     | `MBEU284209-5`  |
| Side            | `B` / `T`       |
| Software version | `V1`           |
| Software file   | `SWX2`          |

---

### Deviation 3 — Single operator token in Serial No. line  
**Status: already handled**

Earlier files had two tokens in parentheses, e.g. `(FP1 - OP01)`. The 22160
files have only one — the physical tester group identifier:

```
Serial No.:22160-BV0001 (AT33)
```

`ParseOperators` sets `operator1 = "AT33"` and `operator2 = ""` (empty). With
the default `stationNameFromOperator=false` configuration, `AT33` is logged as
the UUT operator and no `Operator 2` misc field is written.

When `stationNameFromOperator=true` is set, `AT33` would become the station name.
However, since these files already supply `FP3` via the `Test ID` field, the
`Test ID` value takes precedence, leaving `AT33` unlogged in that configuration.
The recommended setting for the 22160 batch is therefore **`stationNameFromOperator=false`**
(the default), so that both FP3 (station name via `Test ID`) and AT33 (operator)
are captured.

---

### Deviation 4 — Multiple sequential test runs in one file (re-test pattern)  
**Status: already handled**

AT34 files may contain 2 or 3 complete test runs for the same board serial in a
single file, representing re-tests after a failure. Each run has its own block
header, timestamp, and GROUP result. Block counts observed:

| @ blocks per file | Test runs | Occurrence |
|-------------------|-----------|------------|
| 3                 | 1         | 38 AT33 files, some AT34 |
| 6                 | 2         | 37 AT34 files |
| 9                 | 3         | 19 AT34 files |

The existing `BlockEnd` / `RecordStart` state machine handles this correctly:
each block ending `@` submits the current UUT and returns to `InHeader`, and the
next `@` opens a new record. This produces one WATS UUT report per test run, each
with its own timestamp and pass/fail status.

Example: `22160-BV0061 (AT34).ATD` contains a PASS (bottom side, 20:27), a FAIL
(top side, 20:47), and another FAIL (top side, 21:06), resulting in three reports.

---

### Deviation 5 — `OPEN` verdict on failed step lines  
**Status: FIX NEEDED**

One step-level failure was found across the entire 22160 set, in
`22160-BV0035 (AT33).ATD`. The verdict field on a failed step is `"OPEN   "`
rather than `"PASS   "`:

```
000082 "U2" "P6-G" "P42560" 22 19 "0.531 V  " "2.658 V  " "2.658 V  " +20% -20% "OPEN   " "DC-CC" "Range 3" "1.0 ms" "( N, +, N, -)R" 0  0  "**"
```

There are also two structural differences compared to a passing step line:

1. The fixed `"........"` dots field (which indicates a non-checked reference
   value on passing steps) is **replaced** by a repeat of the measured value
   (`"2.658 V  "`), resulting in **three** quoted value fields before the verdict
   instead of two.
2. The verdict string is `"OPEN"` (not `"PASS"` or `"FAIL"`).

**Current behaviour:** The converter's judgement-search loop does not recognise
`"OPEN"` as a verdict token, so `judgementIdx` is never set. The step is
incorrectly logged with status **Passed** and the correct measured value but the
wrong outcome.

**Fix required in `ProcessStepLine`:** Add `"OPEN"` (and defensively `"SHORT"`
and `"FAIL"`) to the judgement recognition condition and map all non-`"PASS"`
verdicts to `StepStatusType.Failed`.

```csharp
// Before (recognises only PASS, FAIL, or dots):
if (v == "PASS" || v == "FAIL" || v.Replace(".", "").Length == 0 || v.Trim() == "......")

// After:
if (v == "PASS" || v == "FAIL" || v == "OPEN" || v == "SHORT" ||
    v.Replace(".", "").Length == 0 || v.Trim() == "......")
```

And the status mapping:

```csharp
// Before:
step.Status = judgement == "FAIL" ? StepStatusType.Failed : StepStatusType.Passed;

// After:
step.Status = (judgement == "PASS") ? StepStatusType.Passed : StepStatusType.Failed;
```

The three-value-field layout (nominal / measured / measured-repeat) requires no
further change because the existing `measureFields` logic already uses only
`measureFields[0]` (nominal) and `measureFields[1]` (measured) for the numeric
test values.

---

### Summary

| # | Deviation | Status |
|---|-----------|--------|
| 1 | `Test ID :FP3` station name in file header | Handled (March 2026) |
| 2 | `_PANEL_` infix in model string | Handled |
| 3 | Single operator token `(AT33)` — no human operator field | Handled |
| 4 | Multiple sequential test runs per file (re-test pattern) | Handled |
| 5 | `OPEN` verdict on failed step lines | Fixed — see below |

**Fix applied (May 2026):** `OPEN` and `SHORT` added to the verdict recognition
set in `ProcessStepLine`. Step status mapping changed from `judgement == "FAIL"`
to `judgement == "PASS"` so any non-PASS verdict (OPEN, SHORT, FAIL) maps to
`StepStatusType.Failed`.

---

## New example set: 340-BM series (AT1 / AT17 / OP686), added May 2026

Eighteen ATD files from customer batch `340` (dated 2019) were added to
`Examples/APT94xx_ATD/`.

---

### Deviation 6 — Date format is `dd/MM/yy`, not `yy/MM/dd`  
**Status: FIXED**

The converter's `DateFormats` array previously listed `"yy/MM/dd HH:mm:ss"`.
The 340- files have dates such as `30/10/19 18:50:21`, which unambiguously
means October 30, 2019 — only parseable correctly as `dd/MM/yy`. With the old
format string the converter parsed this as year=2030, month=10, day=19
(October 19, 2030).

Cross-checking the other example sets confirms this is the universal format:
- `10001` files: `26/01/22` → January 26, 2022 (`dd/MM/yy` ✓), not Jan 22, 2026
- `22160` files: `05/05/26` → May 5, 2026 (`dd/MM/yy` ✓), not May 26, 2005

`DateFormats` corrected to `{ "dd/MM/yy HH:mm:ss", "dd/MM/yy H:mm:ss" }`.
`APT94xx_README.md` updated accordingly.

---

### Deviation 7 — `SHORT` verdict on failed step lines  
**Status: FIXED** (same fix as Deviation 5)

Short-test failures report `"SHORT  "` as the verdict when the tester measures
a resistance below the open-check threshold:

```
000584 "SHRT1711" "P-Auto" "#245-#67" 238 249 "409.5 O " "  1.5 O  " "  1.5 O  " +5% -5% "SHORT  " "DC-CC" ...
```

`"SHORT"` is now included in the verdict recognition set alongside `"OPEN"` and
`"FAIL"`.

---

### Deviation 8 — No `Test ID` line (older firmware)  
**Status: already handled**

340- files are from 2019 and do not contain the `Test ID :` header line. The
station name must therefore be supplied via the `stationName` converter argument,
or left blank. This was already the documented fallback path.

---

### Deviation 9 — `+` operator separator `(OP686+AT21)`  
**Status: no change needed**

Some 340- files encode two operator tokens separated by `+` rather than ` - `:

```
Serial No.:340-BM0003 (OP686+AT21)
```

The same pattern already exists in `20002-SN0005 (OP06+OP01).ATD`. Because
`ParseOperators` uses `" - "` as the separator, it returns the entire inner
string (`"OP686+AT21"`) as `operator1` and an empty `operator2`. The full
token is logged as the UUT operator. No split occurs, but no data is lost.

---

### Deviation 10 — Function code appears before trailing integers (older step layout)  
**Status: already handled**

In 340- step lines the function code (e.g. `"OP"`) is a quoted field that
precedes the two trailing bare integers, whereas newer files place the function
code after them:

| Format | Layout after probe config field |
|--------|---------------------------------|
| Newer (22160) | `0     0     "OP"` |
| Older (340-)  | `"OP" 0     0   ` |

In both cases the function code is the **last quoted field** on the line. The
converter's backward-scan logic (`for i = quoted.Count - 1 downto judgementIdx`)
finds it correctly in both layouts.

---

### Summary (all example sets)

| # | Deviation | Status |
|---|-----------|--------|
| 1 | `Test ID :FP3` station name in file header | Handled (March 2026) |
| 2 | `_PANEL_` infix in model string | Handled |
| 3 | Single operator token `(AT33)` — no human operator field | Handled |
| 4 | Multiple sequential test runs per file (re-test pattern) | Handled |
| 5 | `OPEN` verdict on failed step lines | Fixed (May 2026) |
| 6 | Date format `dd/MM/yy` (was incorrectly `yy/MM/dd`) | Fixed (May 2026) |
| 7 | `SHORT` verdict on failed step lines | Fixed (May 2026) |
| 8 | No `Test ID` line (2019 firmware) | Handled — use `stationName` arg |
| 9 | `+` operator separator `(OP686+AT21)` | Handled — logged as single operator token |
| 10 | Function code before trailing integers (older step layout) | Handled |
