using System.Text.RegularExpressions;

namespace AssignToQa.Helpers
{
    internal class TfsHelper
    {
        /// <summary>
        /// Adds tag to the existing tags. Also checks if it's there already.
        /// </summary>
        /// <param name="existingTags">Existing tags string</param>
        /// <param name="newTag">New tag to be added</param>
        /// <returns>Complete string with old tags + new tag</returns>
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

        /// <summary>
        /// Extracts domain username from string
        /// </summary>
        /// <param name="userNameWithDomain">string with domain, like "First, LastName &lt;Domain\\Name&gt;"</param>
        /// <param name="fetchDomain">true is default, will get domain name. Make it false to get user friendly name</param>
        /// <returns>Domain name</returns>
        public static string GetUserNameFromString(string userNameWithDomain, bool fetchDomain = true)
        {
            var assignedToDomainUserMatch = Regex.Match(userNameWithDomain, "<(?<name>[^>]+)>");
            if (assignedToDomainUserMatch.Success)
            {
                var domainName = assignedToDomainUserMatch.Groups["name"].ToString();
                if (fetchDomain)
                {
                    return domainName;
                }

                // Otherwise get user friendly name
                var domainNameIndex = assignedToDomainUserMatch.Index;
                return userNameWithDomain.Remove(domainNameIndex).Trim();
            }
            return null;
        }
    }
}