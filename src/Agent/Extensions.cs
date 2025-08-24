namespace JMW.Agent.Client;

public static class Extensions
{
    public static T? GetSection<T>(this IConfiguration config)
    {
        var name = typeof(T).Name;
        if (name.EndsWith("Options", StringComparison.Ordinal))
        {
            name = name[0..^7];
        }
        return config.GetSection(name).Get<T>();
    }

    public static IConfigurationSection Section<T>(this IConfiguration config)
    {
        var name = typeof(T).Name;
        if (name.EndsWith("Options", StringComparison.Ordinal))
        {
            name = name[0..^7];
        }
        return config.GetSection(name);
    }
}