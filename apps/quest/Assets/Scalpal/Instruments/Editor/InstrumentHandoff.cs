using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Instruments.Editor
{
    public static class InstrumentHandoff
    {
        // Export path stays outside Assets. No private machine path is stored in the package.
        public static void ValidateAndExport()
        {
            InstrumentAssetBuilder.BuildAll();
            InstrumentRuntimeValidation.Run();
            ExportCurrent();
        }

        public static void ExportCurrent()
        {
            var output = Environment.GetEnvironmentVariable("SCALPAL_INSTRUMENT_PACKAGE");
            if (string.IsNullOrEmpty(output) || !Path.IsPathRooted(output))
                throw new InvalidOperationException("SCALPAL_INSTRUMENT_PACKAGE must be an absolute output path.");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            AssetDatabase.ExportPackage("Assets/Scalpal/Instruments", output, ExportPackageOptions.Recurse);
            Debug.Log("SCALPAL_INSTRUMENT_PACKAGE_OK: " + new FileInfo(output).Length + " bytes");
        }
    }
}
