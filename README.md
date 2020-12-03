# WATS Converter - TAKAYA Flying Probe
A WATS Client converter plugin for importing test data from TAKAYA Flying Probe test system to WATS.

## Getting Started

* [About WATS](https://wats.com/manufacturing-intelligence/)
* [About submitting data to WATS](https://virinco.zendesk.com/hc/en-us/articles/207424613)
* [About creating custom converters](https://virinco.zendesk.com/hc/en-us/articles/207424593)

## Download

You can download the latest released version of the converter [here](https://github.com/Virinco/WATS-Client-Converter-TAKAYA-Flying-Probe/releases/latest). See the Custom Converter section in the [WATS Client Installation Guide](https://wats.com/download) for your version of the WATS Client for how to install a converter.

### Test-sofware configuration

This is an example converter for TAKAYA Flying Probe that only supports output in the same format as the example files. Different configurations of the test software will require an accordingly different converter.

### Parameters

This converter uses the following parameters:

| Parameter         | Default value         | Description                                                    |
|-------------------|-----------------------|----------------------------------------------------------------|
| partNumber        | ABC123                | If log is missing a part number, use this one.                 |
| partRevision      | 1.0                   | If log is missing a revision, use this one.                    |
| sequenceName      | MainSequence          | If log is missing sequence name, use this one.                 |
| sequenceVersion   | 1.0.0                 | If log is missing sequence version, use this one.              |
| operationTypeCode | 10                    | If log is missing operation code (process code), use this one. |
| operator          | sysoper               | If log is missing operator, use this one.                      |
| stationName       | {Client station name} | If log is missing station name, use this one.                  |
| testModeType      | Active                | Sets the mode to Active (computes status) or Import.           |
| cultureCode       | en-US                 | Culture used for parsing numbers.                              |
| fileEncoding      | 1252                  | File encoding of the file to import.                           |
| validationMode    | ThrowExceptions       | ThrowExceptions or AutoTruncate.                               |
| converterFormat   | B						| A = without Net_Name, B = with Net_Name

## Testing

The project uses the [MSTest framework](https://docs.microsoft.com/en-us/visualstudio/test/quick-start-test-driven-development-with-test-explorer) for testing the converter.

It is setup with two tests; one for setting up the API by registering the client to your WATS, and one for running the converter.

The values are hardcoded in the test, so you will need to change the values to reflect your setup.
* In SetupClient, fill in your information in the the call to RegisterClient.
* In TestConverter, fill in the path to the file you want to test the converter with. There are example files in the Examples folder.
* Run SetupClient once, then you can run TestConverter as many times as you want.

## Contributing

We're open to suggestions! Feel free open an issue or create a pull request.

Please read [Contributing](CONTRIBUTING.md) for details on contributions.

## Contact

* Issues with the converter or suggestions for improvements can be submitted as an issue here on GitHub.
* Ask questions about WATS in the [WATS Community Help](https://virinco.zendesk.com/hc/en-us/community/topics/200229613)
* Suggestions for the WATS Client itself or WATS in general can be submitted to the [WATS Idea Exchange](https://virinco.zendesk.com/hc/en-us/community/topics/200229623)
* Sensitive installation issues or other sensitive questions can be sent to [support@virinco.com](mailto://support@virinco.com)

## License

This project is licensed under the [LGPLv3](COPYING.LESSER) which is an extention of the [GPLv3](COPYING).