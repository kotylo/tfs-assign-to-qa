using AssignToQa.Constants;
using Newtonsoft.Json.Linq;

namespace AssignToQa.Data
{
    public class UpdateWorkItemRequest
    {
        public int WorkItemId { get; set; }
        public string Tags { get; set; }
        public string AssignedTo { get; set; }
        public JToken ExistingFields { get; set; }
        public string Title { get; set; }
        public Tfs.State State { get; set; }
    }
}