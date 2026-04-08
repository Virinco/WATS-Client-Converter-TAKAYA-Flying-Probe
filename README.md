# WATS Converter - TAKAYA Flying Probe
A WATS Client converter plugin for importing test data from TAKAYA Flying Probe test systems to WATS.

Supports two TAKAYA output formats:
- **ATD** (tab-delimited, `.ATD`) — handled by `ATD_Converter`
- **ATDX** (semicolon-delimited, newer format, `.atdx`) — handled by `ATDX_Converter`

## Getting Started

* [About WATS](https://wats.com/manufacturing-intelligence/)
* [About submitting data to WATS](https://virinco.zendesk.com/hc/en-us/articles/207424613)
* [About creating custom converters](https://virinco.zendesk.com/hc/en-us/articles/207424593)

## Download

You can download the latest released version of the converter [here](https://github.com/Virinco/WATS-Client-Converter-TAKAYA-Flying-Probe/releases/latest). See the Custom Converter section in the [WATS Client Installation Guide](https://wats.com/download) for your version of the WATS Client for how to install a converter.

Two pre-built DLL variants are published with each release:

| Build | Target framework | NuGet package used |
|-------|------------------|--------------------|
| `net472` | .NET Framework 4.7.2 | `WATS.Client` 6.x |
| `net8.0-windows` | .NET 8.0 (Windows) | `Virinco.WATS.ClientAPI` + `Virinco.WATS.StandardConverters` 7.x |

Use the `net472` build with WATS Client installations based on .NET Framework. Use the `net8.0-windows` build with WATS Client installations based on .NET 8.

### Converters

#### ATD_Converter
Parses the classic TAKAYA ATD format (tab-delimited). The header section starts with `@`, groups are delineated by `* GROUP No.x *` markers, and each test step is a tab-separated data row.

#### ATDX_Converter
Parses the newer TAKAYA ATDX format (semicolon-delimited). The header section uses `#############"Main header"############` / `#############"Group header"#########` markers. Each test step is a semicolon-delimited data row matching the column header `Order;Aux;M.Aux;Parts;Value;...`.

### Test-software configuration

These are example converters for TAKAYA Flying Probe. They support output in the same format as the files in the `Examples` folder. Different configurations of the test software may produce variations that require converter adjustments.

### Parameters

Both converters share the following parameters:

| Parameter          | Default value         | Description                                                    |
|--------------------|-----------------------|----------------------------------------------------------------|
| partNumber         | ABC123                | If log is missing a part number, use this one.                 |
| partRevision       | 1.0                   | If log is missing a revision, use this one.                    |
| sequenceName       | MainSequence          | If log is missing sequence name, use this one.                 |
| sequenceVersion    | 1.0.0                 | If log is missing sequence version, use this one.              |
| operationTypeCode  | 10                    | If log is missing operation code (process code), use this one. |
| operator           | sysoper               | If log is missing operator, use this one.                      |
| stationName        | {Client station name} | If log is missing station name, use this one.                  |
| testModeType       | Active                | Sets the mode to Active (computes status) or Import.           |
| cultureCode        | en-US                 | Culture used for parsing numbers.                              |
| fileEncoding       | 1252                  | File encoding of the file to import.                           |
| validationMode     | ThrowExceptions       | ThrowExceptions or AutoTruncate.                               |
| UnitCalcPreference | Measures              | `Measures` or `Limits` — controls which unit is used as the basis when ref and measured units differ. |
| GroupByComponentType | false               | `true` or `false` — when `true`, steps are grouped into sub-sequences by component type (e.g. `POWER_SHORTS`, `BOARD_SHORTS` for ATDX; `C`, `R`, `U` etc. for ATD). Step names always include the net names for unique identification. |

## Testing

The project uses the [MSTest framework](https://docs.microsoft.com/en-us/visualstudio/test/quick-start-test-driven-development-with-test-explorer) for testing the converters.

Two test methods are provided:
- `SetupClient` — registers your WATS server credentials (run once).
- `TestATDConverter` — loops over all `.ATD` files in `Examples\ATD\` and submits each using `ATD_Converter`.
- `TestATDXConverter` — loops over all `.atdx` files in `Examples\ATDX\` and submits each using `ATDX_Converter`.

Each converter calls `apiRef.Submit(currentUUT)` directly during import, so reports are sent to the server immediately. Do **not** call `SubmitPendingReports()` after importing — it would re-process every file through the converter a second time and cause duplicate submissions.

To run the tests:
* In `SetupClient`, fill in your server, username, and password in the call to `RegisterClient`.
* Run `SetupClient` once to register the client.
* Run `TestATDConverter` and/or `TestATDXConverter` as needed.

## Contributing

We're open to suggestions! Feel free to open an issue or create a pull request.

Please read [Contributing](CONTRIBUTING.md) for details on contributions.

## Troubleshooting

#### Converter failed to start

Symptom:
* Converter did not start after being configured.

Possible cause:
* WATS Client Service does not have folder permission to the input path.
* WATS Client Service was not restarted after configuration.

Possible solution:
* [Give NETWORK SERVICE write permission to the input path folder](https://virinco.zendesk.com/hc/en-us/articles/207424113-WATS-Client-Add-write-permission-to-NETWORK-SERVICE-on-file-system-to-allow-converter-access)
* Make a change in a converter configuration and undo the change, click APPLY. When asked to restart the service, click Yes.

#### Converter class drop down list is empty

Symptom:
* The converter class drop down list in the Client configurator is empty after adding a converter DLL.

Possible cause:
* The DLL file is blocked. Windows blocks files that it thinks are untrusted, which stops them from being executed.

Possible solution:
* Open properties on the file and unblock it.

#### Other

Contact Virinco support, and include the wats.log file: [Where to find the wats log file at the Client](https://virinco.zendesk.com/hc/en-us/articles/207424033-Where-to-find-the-wats-log-file-at-the-Client).

## Contact

* Issues with the converter or suggestions for improvements can be submitted as an issue here on GitHub.
* Ask questions about WATS in the [WATS Community Help](https://virinco.zendesk.com/hc/en-us/community/topics/200229613)
* Suggestions for the WATS Client itself or WATS in general can be submitted to the [WATS Idea Exchange](https://virinco.zendesk.com/hc/en-us/community/topics/200229623)
* Sensitive installation issues or other sensitive questions can be sent to [support@virinco.com](mailto://support@virinco.com)

## License

This project is licensed under the [LGPLv3](COPYING.LESSER) which is an extention of the [GPLv3](COPYING).
