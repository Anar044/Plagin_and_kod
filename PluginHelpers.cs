using LinqToDB;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Resto.Front.Api.Data.Organization;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Xml.Linq;
using ITable = Resto.Front.Api.Data.Organization.Sections.ITable;

namespace Resto.Front.Api.HorecaControlPlugin;

public static class PluginHelpers
{
    public static readonly string VersionDB = "002";
    public static IRestaurant DepartmentName = null;

    public static bool IsDeveloperMode = false;

    public static ITerminalsGroup GroupName = null;

    public static List<Guid> ExcludedPayments;

    // Таймер времени срабатывания таска в OrderChangeNotifier
    public static readonly double TimerOrderTimeout = 1;
    public static readonly string FrontName = "HorecaControl";


    public static string StorageDirectory
    {
        get
        {
            var programData =
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "HorecaControl");
            if (!Directory.Exists(programData))
                Directory.CreateDirectory(programData);
            return programData;
        }
    }


    public static XElement GetElement(this XDocument doc, string elementName)
    {
        foreach (XNode node in doc.DescendantNodes())
        {
            if (node is XElement)
            {
                XElement element = (XElement)node;
                if (element.Name.LocalName.Equals(elementName))
                    return element;
            }
        }

        return null;
    }

    public static string GetTablesAsString(this IReadOnlyList<ITable> tables)
    {
        string result = string.Empty;
        try
        {
            if (tables != null && tables.Any())
            {
                // Format: "201(patek philippe), 202(Hublot)"
                result = string.Join(", ", tables
                    .Select(x =>
                    {
                        if (!string.IsNullOrWhiteSpace(x.Name))
                            return $"{x.Number}({x.Name})";
                        return x.Number.ToString();
                    })
                    .ToList());
            }
        }
        catch
        {
        }

        return result;
    }


    #region ZIP string

    public static byte[] ToGZip(this object obj)
    {
        if (obj == null)
        {
            return null;
        }

        string json = obj.ToJson();
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            var inputBytes = Encoding.UTF8.GetBytes(json);
            using var memoryStream = new MemoryStream();
            using (var gzipStream = new GZipStream(memoryStream, CompressionMode.Compress))
            {
                gzipStream.Write(inputBytes, 0, inputBytes.Length);
            }

            return memoryStream.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    #endregion

    #region ILog extension

    public static void Debug(this ILog log, object data, bool debug, [CallerMemberName] string ClassName = "",
        [CallerLineNumber] long LineNumber = 0, [CallerFilePath] string filename = "")
    {
        if (Properties.Settings.Default.Debug)
        {
            string jsonSer = data.ToJson();
            log.Debug(
                $"{ClassName} (file:{System.IO.Path.GetFileName(filename)} , line {LineNumber}) ::\n{jsonSer}");
        }
    }

    public static void Debug(this ILog log, string text, bool debug)
    {
        if (debug)
            log.Info($"(EXDEBUG) {text}");
    }

    public static void Debug(this ILog log, string text)
    {
        if (Properties.Settings.Default.Debug)
            log.Info($"(DEBUG) {text}");
    }

    #endregion


    #region JSON

    public static JsonSerializerSettings jsonSerializerSettings = new JsonSerializerSettings
    {
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None,
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        PreserveReferencesHandling = PreserveReferencesHandling.None,
        Converters = { new StringEnumConverter() },
        // Без сортировки свойств — как в рабочем плагине hc_250305 (порядок объявления).
    };

    internal static string ToJson(this object obj)
    {
        var result = string.Empty;
        try
        {
            result = JsonConvert.SerializeObject(obj, jsonSerializerSettings);
        }
        catch (Exception ex)
        {
            PluginContext.Log.Error($"ToJson :: {ex.Message}", ex);
        }

        return result;
    }


    internal static T FromJson<T>(this string str) where T : class
    {
        T result = null;
        try
        {
            if (string.IsNullOrWhiteSpace(str))
            {
                PluginContext.Log.Warn($"FromJson :: Input string is null or empty for type {typeof(T).Name}");
                return null;
            }

            result = JsonConvert.DeserializeObject<T>(str, jsonSerializerSettings);
        }
        catch (Exception ex)
        {
            PluginContext.Log.Error($"FromJson :: {ex.Message}", ex);
            PluginContext.Log.Error($"FromJson :: Input string (first 200 chars): {(str?.Length > 200 ? str.Substring(0, 200) : str ?? "null")}");
        }

        return result;
    }

    /// <summary>
    /// Десериализация для динамических типов (используется в конвертерах)
    /// </summary>
    internal static object FromJsonDynamic(Type type, string str)
    {
        object result = null;
        try
        {
            if (string.IsNullOrWhiteSpace(str))
            {
                PluginContext.Log.Warn($"FromJsonDynamic :: Input string is null or empty for type {type?.Name ?? "null"}");
                return null;
            }

            result = JsonConvert.DeserializeObject(str, type, jsonSerializerSettings);
        }
        catch (Exception ex)
        {
            PluginContext.Log.Error($"FromJsonDynamic :: {ex.Message}", ex);
            PluginContext.Log.Error($"FromJsonDynamic :: Type: {type?.Name ?? "null"}");
            PluginContext.Log.Error($"FromJsonDynamic :: Input string (first 200 chars): {(str?.Length > 200 ? str.Substring(0, 200) : str ?? "null")}");
        }

        return result;
    }

    #endregion

    public static string GetEnumMemberValue<T>(this T value)
        where T : Enum
    {
        return typeof(T)
            .GetTypeInfo()
            .DeclaredMembers
            .SingleOrDefault(x => x.Name == value.ToString())
            ?.GetCustomAttribute<EnumMemberAttribute>(false)
            ?.Value;
    }
}
