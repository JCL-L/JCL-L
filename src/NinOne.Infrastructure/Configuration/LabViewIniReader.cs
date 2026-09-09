using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NinOne.Infrastructure.Configuration
{
    public sealed class LabViewIniReader
    {
        private readonly string _path;
        private readonly Dictionary<string, Dictionary<string, string>> _sections;

        private LabViewIniReader(string path, Dictionary<string, Dictionary<string, string>> sections)
        {
            _path = path;
            _sections = sections;
        }

        public static LabViewIniReader Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("INI 路径不能为空。", nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException("找不到 INI 配置文件。", path);

            var bytes = File.ReadAllBytes(path);
            var text = Decode(bytes);
            var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            var currentSection = string.Empty;

            using (var reader = new StringReader(text))
            {
                string line;
                var lineNumber = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith(";", StringComparison.Ordinal) || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
                    if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                    {
                        currentSection = trimmed.Substring(1, trimmed.Length - 2).Trim();
                        if (!sections.ContainsKey(currentSection)) sections[currentSection] = new Dictionary<string, string>(StringComparer.Ordinal);
                        continue;
                    }

                    var equals = line.IndexOf('=');
                    if (equals < 1) throw new FormatException(string.Format("INI 格式错误：文件 {0}，第 {1} 行缺少键或等号。", path, lineNumber));
                    if (!sections.ContainsKey(currentSection)) sections[currentSection] = new Dictionary<string, string>(StringComparer.Ordinal);
                    var key = line.Substring(0, equals).Trim();
                    var value = line.Substring(equals + 1).Trim();
                    if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"') value = value.Substring(1, value.Length - 2);
                    sections[currentSection][key] = value;
                }
            }

            return new LabViewIniReader(path, sections);
        }

        public string Required(string section, string key, string expectedType)
        {
            Dictionary<string, string> values;
            string value;
            if (!_sections.TryGetValue(section, out values) || !values.TryGetValue(key, out value) || string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException(string.Format("配置缺失：文件“{0}”，节“{1}”，键“{2}”，期望类型“{3}”。", _path, section, key, expectedType));
            }
            return value.Trim();
        }

        public string Optional(string section, string key, string fallback)
        {
            Dictionary<string, string> values;
            string value;
            return _sections.TryGetValue(section, out values) && values.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : fallback;
        }

        public int RequiredInt(string section, string key, int minimum, int maximum)
        {
            var value = Required(section, key, "整数");
            int result;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) || result < minimum || result > maximum)
            {
                throw new InvalidDataException(string.Format("配置无效：文件“{0}”，节“{1}”，键“{2}”，值“{3}”，期望 {4}～{5} 的整数。", _path, section, key, value, minimum, maximum));
            }
            return result;
        }

        public bool RequiredBoolean(string section, string key)
        {
            var value = Required(section, key, "TRUE/FALSE 布尔值");
            if (string.Equals(value, "TRUE", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(value, "FALSE", StringComparison.OrdinalIgnoreCase)) return false;
            throw new InvalidDataException(string.Format("配置无效：文件“{0}”，节“{1}”，键“{2}”，值“{3}”，期望 TRUE 或 FALSE。", _path, section, key, value));
        }

        private static string Decode(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(936).GetString(bytes);
            }
        }
    }
}
