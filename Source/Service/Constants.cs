namespace Glasswall.CloudSdk.AWS.Rebuild
{
    public static class Constants
    {
        public const string MACOSX = "__MACOSX";
        public const string UPLOADS_FOLDER = "uploads";
        public const string STATUS_FILE = "GlasswallFileProcessingStatus.txt";
        public const string OPEN = "OPEN";
        public const string BASH = "bash";
        public const string RESPMOD_HEADERS = "RESPMOD HEADERS:";
        public const string OK = "200 OK";

        public static class EnvironmentVariables
        {
            public const string AWS_ACCESS_KEY_ID = "AWS_ACCESS_KEY_ID";
            public const string AWS_SECRET_ACCESS_KEY = "AWS_SECRET_ACCESS_KEY";
        }

        public static class GwIcap
        {
            public const string ARGUMENT = "-c \"cd {0}; time /usr/local/c-icap/bin/c-icap-client -f ./{1} -i 127.0.0.1 -p 1344 -s gw_rebuild -o {2} -v;\"";
        }
    }
}
