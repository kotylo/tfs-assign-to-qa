using System.ComponentModel;

namespace AssignToQa.Constants
{
    public class Tfs
    {
        public class ActivityNames
        {
            public const string DevelopmentActivityName = "Development";
            public const string TestingActivityName = "Testing";
        }

        public enum State
        {
            None = 0,

            [Description("Done")]
            Done = 1,

            [Description("In Progress")]
            InProgress = 2,

            [Description("Removed")]
            Removed = 3,

            [Description("To Do")]
            ToDo = 4,
        }
    }
}
