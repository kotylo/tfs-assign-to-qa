using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AssignToQa.Data
{
    class DeveloperHasNotFinishedTaskException : Exception
    {
        public DeveloperHasNotFinishedTaskException(string devName)
        {
            DeveloperName = devName;
        }
        public string DeveloperName { get; set; }
    }
}
