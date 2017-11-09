namespace AssignToQa.Helpers
{
    internal class TfsHelper
    {
        public static string AddTag(string existingTags, string newTag)
        {
            if (!existingTags.Contains(newTag))
            {
                var preChar = string.Empty;
                if (!string.IsNullOrWhiteSpace(existingTags))
                {
                    preChar = "; ";
                }
                existingTags += $"{preChar}{newTag}";
            }
            return existingTags;
        }
    }
}