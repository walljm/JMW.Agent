namespace JMW.Agent.Client
{
    public static class Extensions
    {
        public static T GetSection<T>(this IConfiguration config)
        {
            var name = typeof(T).Name;
            if (name.EndsWith("Options"))
            {
                name = name[0..^7];
            }
#pragma warning disable CS8603 // Possible null reference return.
            return config.GetSection(name)
                .Get<T>();
#pragma warning restore CS8603 // Possible null reference return.
        }

        public static IConfigurationSection Section<T>(this IConfiguration config)
        {
            var name = typeof(T).Name;
            if (name.EndsWith("Options"))
            {
                name = name[0..^7];
            }
            return config.GetSection(name);
        }
    }
}