# Third-party components

## ONNX Runtime

Microsoft.ML.OnnxRuntime 1.32.0 and Microsoft.ML.OnnxRuntime.Managed 1.32.0.
Source: https://github.com/microsoft/onnxruntime ; license: MIT.
The exact dependency graph is recorded in packages.lock.json. Native binaries and
their upstream notices are distributed by the official Microsoft NuGet package.
ORT_DISABLE_TELEMETRY=1 is set for this process before native initialization,
and OrtEnv.DisableTelemetryEvents is also called before creating a session.

## ddddocr OCR model and character table

Source: https://github.com/sml2h3/ddddocr
Commit: c40f56f95412e10bcb9bd0bd24411e92f896d238
Files: common_old.onnx; old character table extracted from models/charset_manager.py.
License: MIT, in assets/models/LICENSE-ddddocr.txt in source, embedded in the EXE's license viewer.
Checksums: assets/models/manifest.json; the embedded model and character table are verified before inference.
Only local ONNX inference is used; no Python package, API server, advertising,
captcha service, slider functionality, or outbound connection is included.

## .NET

The self-contained package includes the Microsoft .NET 10 runtime under the
licenses and third-party notices provided by Microsoft in the distribution.
Source: https://github.com/dotnet ; license: MIT and the accompanying notices.

## Windows SDK .NET projections / C#/WinRT

Microsoft.Windows.SDK.NET.Ref 10.0.19041.57 supplies the Windows Runtime projections
and runtime used for Windows.Graphics.Capture. It is an official Microsoft SDK
package; its package metadata specifies the Windows SDK license terms:
https://aka.ms/WinSDKLicenseURL . C#/WinRT source: https://github.com/microsoft/CsWinRT .
This targeting-pack version is selected by the pinned .NET SDK in global.json
and its Microsoft.NETCoreSdk.BundledVersions.props, rather than the app's NuGet lock graph.
