using System;
using System.IO;
using Newtonsoft.Json;

namespace Resto.Front.Api.HorecaControlPlugin;

public class FileConfig
{
    [JsonProperty("PluginId")]
    public Guid? PluginId { get; set; }


    private static string fileName = "smarthoreca.json";

    private static string path =
        Path.Combine(PluginContext.Integration.GetConfigsDirectoryPath(), fileName);


    public static FileConfig GetConfigStorageConfig()
    {
        var configDefault = new FileConfig
        {
            PluginId = Guid.NewGuid(),
        };

        FileConfig result = null;

        var file = Path.Combine(PluginHelpers.StorageDirectory, fileName);
        if (File.Exists(file))
        {
            result = File.ReadAllText(file).FromJson<FileConfig>();
        }
        else
        {
            File.WriteAllText(file, configDefault.ToJson());
            result = configDefault;
        }

        return result;
    }


    public static FileConfig GetConfig()
    {
        FileConfig result = null;
        try
        {
            if (File.Exists(path))
            {
                result = File.ReadAllText(path).FromJson<FileConfig>();
            }
            else
            {
                File.WriteAllText(path, new FileConfig().ToJson());
            }
        }
        catch (Exception ex)
        {
            if (Properties.Settings.Default.Debug)
                PluginContext.Log.Error($"GetConfig  :: {ex.Message}", ex);
            else
                PluginContext.Log.Error($"GetConfig  :: {ex.Message}");
        }

        return result;
    }
}

public class DebugSettings
{
    //C1s$0%44a
    public string DebugSecretString { get; set; }

    // http://157.90.213.116:9580/resto
    public string DebugServerUrl { get; set; }

    // 1eaf2d63-91c5-cd26-018f-aadcfd250010
    public Guid? DebugDepartmentId { get; set; }

    // http://68.233.120.197/plugin-websocket
    public string DebugSocketUrl { get; set; }

    // c4h4nG1R4nd1G0Rm4d37h1s4ppl1ca710nf0rsm4r7h0r3c4
    public string DebugUsername { get; set; }

    // f33ls0rryf0rm4nwh0w1LLh4V370r34d7h37
    public string DebugPassword { get; set; }

    public static DebugSettings GetDebugSettings()
    {
        DebugSettings result = null;
        var file = Path.Combine(PluginHelpers.StorageDirectory, "debug.json");
        if (File.Exists(file))
        {
            result = File.ReadAllText(file).FromJson<DebugSettings>();
        }

        return result;
    }
}
