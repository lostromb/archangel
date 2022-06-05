using Durandal.Common.IO.Json;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Archangel
{
    public class MonitorState
    {
        /// <summary>
        /// The last time that the time remaining was updated
        /// </summary>
        public DateTimeOffset LastUpdateTime { get; set; }

        /// <summary>
        /// The last time that the time remaining was read out to the user
        /// </summary>
        public DateTimeOffset? LastReadoutTime { get; set; }

        /// <summary>
        /// The amount of use time granted per day
        /// </summary>
        [JsonConverter(typeof(JsonTimeSpanStringConverter))]
        public TimeSpan TimeAllotmentPerDay { get; set; }

        /// <summary>
        /// The amount of time allotment remaining for the current day
        /// </summary>
        [JsonConverter(typeof(JsonTimeSpanStringConverter))]
        public TimeSpan TimeRemainingToday { get; set; }

        /// <summary>
        /// Whether the monitor is enabled
        /// </summary>
        public bool Enabled { get; set; }
    }
}
