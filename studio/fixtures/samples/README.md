# Sample messages

Authoring source of the factory's test samples. The seed publishes each file (not this README) to the
content branch's `samples/`, where the studio reads it (R-11-25).

Synthetic sample messages for the factory tests. Every value is invented: no person, employer or
bank in these files exists. IBANs use the UAE format with computed check digits.

A line that starts with `# file:` gives the name the file had on the channel. It is fixture
metadata, not part of the message, and the intake maps read it as the `SourceFile` fact.

| File | Format | Used by |
| --- | --- | --- |
| `wps-sif-valid.sif` | WPS SIF, SCR first | WPS-MAP-001, WPS-OUT-001 to 003 |
| `wps-sif-valid-scr-last.sif` | WPS SIF, SCR last, two records | WPS-MAP-002 |
| `wps-sif-bad-control-total.sif` | WPS SIF, control total wrong | WPS-OUT-004 |
| `wps-sif-bad-iban.sif` | WPS SIF, IBAN check digits wrong | WPS-OUT-005 |
| `wps-pif-valid.pif` | WPS PIF, the factory's first layout (PDR) | WPS-IN-001 to 007 |
| `wps-pif-ssm.pif` | WPS PIF, the source's layout (EDR, EVP, PCR) | WPS-IN-008, the native inward end to end |
| `wps-rtc-valid.rtc` | WPS RTC, the factory's provisional layout (RTH, RTD) | WPS-RTC-001 |
| `wps-sif-bad-month.sif` | WPS SIF, salary month 13 | Kept as an invalid sample; the leg test that used it was replaced |
| `wps-sif-two-lines.sif` | WPS SIF, two lines of 1000 and 500, total 1500 | The WPS end-to-end test: the bank's limit on each line and on the file total |
| `wps-sif-upload-evp.sif` | WPS SIF with an EVP record, and the channel's upload declared before the records (`# channel:`, `# initiated-by:`, `# debtor-name:`, `# debtor-account:`) | WPS-MAP-003 (Issue I-100) |
| `wps-sif-large-total.sif` | WPS SIF whose total has sixteen significant digits | WPS-MAP-004 (Issue I-109) |
| `wps-sif-upload-account-type.sif` | WPS SIF whose channel declared the lines' account type at upload (`# account-type:`), two lines at two routing codes | WPS-MAP-005 (Issue I-115) |
| `wps-sif-no-file-line.sif` | WPS SIF with no `# file:` line, as a bank's channel drops it or posts it to the File API: its name comes from the File API (`fileName`) | WPS-MAP-006, WPS-MAP-007 (Issue I-195), the laptop test |

Format of a SIF line, comma separated:

- `SCR,<employer id 13>,<employer bank routing code 9>,<creation date>,<creation time>,<salary month MMYYYY>,<record count>,<total salary>,<currency>,<employer reference>`
- `EDR,<person id 14>,<agent routing code 9>,<IBAN>,<pay start>,<pay end>,<days in period>,<fixed pay>,<variable pay>,<leave days>`
- `EVP,<person id 14>,<agent routing code 9>,<housing>,<conveyance>,<medical>,<annual passage>,<overtime>,<other>,<leave encashment>` (optional, the breakdown of an EDR's variable pay; read by `uae.wps.map.sif-in.v1` from 1.1.0)

A line that starts with `# channel:`, `# initiated-by:`, `# debtor-name:` or `# debtor-account:` is what the employer's channel declared beside the file at upload. Like `# file:`, it is not part of the message.

`wps-rri-valid.rri` is a refund request instruction from WPS to the bank as the employees' bank, in the source's layout (TransactHub `InwardRriParser`): the name's characters 4 to 6 are the bank's agent code (`999`, the example bank's synthetic value of `uae.wps.map.rri-in.v1#fileNameAgentCode`), 7 to 19 the employer's establishment and its last twelve the WPS file; then ten fields a line:

- `DRR,<establishment 13>,<person id 14>,<refund amount>,<reason code>,<payroll file name>,<payroll WPS file reference>,<account>, ,EWPMS`
- `DRC,,<WPS reference>,<total>,<count>, , , , ,EWPMS`

The SIF layout follows the public bank guides the factory pack was written from. It is
provisional, as `uae.wps` is.
