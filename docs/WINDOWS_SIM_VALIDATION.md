# Windows SIM validation harness

This guide validates one generated answer file against one administrator-supplied Windows image. It is a Windows-host-only workflow. It does not mount, modify, format, or copy the ISO, WIM, ESD, disk, or USB drive.

Windows SIM is part of the Windows ADK. Microsoft requires manually authored answer files to be revalidated in Windows SIM because available settings can change. Windows SIM compares an answer file with the settings available in the selected Windows image. [Windows SIM technical reference](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/wsim/windows-system-image-manager-technical-reference), [answer-file best practices](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/wsim/best-practices-for-authoring-answer-files), [Validate an Answer File](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/wsim/validate-an-answer-file)

## Inputs and boundaries

Provide all four inputs outside the Easyaller repository:

- The official Windows ISO supplied by the administrator.
- The exact `install.wim` or `install.esd` taken from that ISO.
- The selected image index for the intended Pro or Enterprise edition.
- A generated `autounattend.xml`.

The evidence JSON is written to `%LOCALAPPDATA%\Easyaller\Validation` by default. The script rejects an output path inside the repository. ISO, WIM, ESD, generated XML, catalog, and evidence files stay out of Git.

## Run the automated preflight

Run PowerShell on a Windows technician machine with DISM available. This is read-only inspection plus XML policy validation.

```powershell
.\scripts\Validate-AnswerFile.ps1 `
  -InstallationMedia 'D:\Images\Windows11.iso' `
  -WindowsImage 'D:\Images\install.wim' `
  -ImageIndex 6 `
  -AnswerFile 'D:\Easyaller-output\autounattend.xml'
```

The script verifies the XML root and the absence of prohibited sections, then records SHA-256 hashes for the ISO, image, and answer file. It calls DISM for the selected image index and saves its metadata and raw output in the evidence JSON. It does not create a catalog or declare Windows SIM success.

## Complete Windows SIM validation

1. Use the Windows ADK installation that matches the technician environment and open Windows System Image Manager.
2. Open a copy of the selected `install.wim` or catalog file from a writable technician directory. Windows SIM creates or refreshes the catalog for the image it opens.
3. Select the same image index recorded by the preflight evidence.
4. Open the generated `autounattend.xml` in the Answer File pane.
5. Choose **Tools** then **Validate Answer File**.
6. Record the exact ADK or Windows SIM version and the validation outcome. A successful result means no warnings or errors appear in the Validation messages pane.

Microsoft documents this as an interactive Windows SIM action. The harness intentionally does not invent a command-line success signal for it.

After the manual step, create final evidence:

```powershell
.\scripts\Validate-AnswerFile.ps1 `
  -InstallationMedia 'D:\Images\Windows11.iso' `
  -WindowsImage 'D:\Images\install.wim' `
  -ImageIndex 6 `
  -AnswerFile 'D:\Easyaller-output\autounattend.xml' `
  -WindowsSimResult Passed `
  -WindowsSimVersion 'Windows ADK 10.1.x, Windows SIM' `
  -WindowsSimMessage 'No warnings or errors in the Validation pane.'
```

Use `Failed` and copy the messages into `WindowsSimMessage` when Windows SIM reports a warning or error. Do not label a catalog entry as `SchemaValidated` until a `Passed` evidence file exists for the exact ISO hash, image hash, image index, and answer-file hash.

## Limits

This is schema validation, not a VM or physical-PC test. A result of `Passed` does not validate OOBE behavior, first boot, network conditions, account cleanup, or a USB workflow. Those require later VM and physical-PC evidence.
