using System.Management;
using System.Runtime.InteropServices;

namespace JMW.Agent.Common.Models;

public static class Extensions
{
    public static T? ToType<T>(this ManagementObject obj)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return default;
        }

        var o = Activator.CreateInstance<T>();
        var type = typeof(T);
        foreach (var prop in obj.Properties)
        {
            var p = type.GetProperty(prop.Name);
            if (p == null)
            {
                continue;
            }
            if (prop.Type == CimType.None || prop.Value == null)
            {
                continue;
            }

            if (prop.IsArray)
            {
                var src = (Array)prop.Value;
                if (src is null)
                {
                    return default;
                }

                var arr = Activator.CreateInstance(p.PropertyType, src.Length) as Array;
                if (arr is null)
                {
                    return default;
                }

                var et = p.PropertyType.GetElementType();
                if (et is null)
                {
                    return default;
                }

                for (var i = 0; i < src.Length; i++)
                {
                    var v = et.IsEnum ? Enum.Parse(et, src.GetValue(i)?.ToString() ?? string.Empty, true)
                        : Convert.ChangeType(src.GetValue(i), et);
                    arr.SetValue(v, i);
                }
                p.SetValue(o, arr);
            }
            else
            {
                if (p.PropertyType.IsEnum)
                {
                    p.SetValue(o, Enum.Parse(p.PropertyType, prop.Value?.ToString() ?? string.Empty, true));
                }
                else if (p.PropertyType == typeof(DateTime?) || p.PropertyType == typeof(DateTime))
                {
                    p.SetValue(o, ManagementDateTimeConverter.ToDateTime(prop.Value?.ToString() ?? string.Empty));
                }
                else
                {
                    p.SetValue(o, prop.Value);
                }
            }
        }

        return o;
    }
}
