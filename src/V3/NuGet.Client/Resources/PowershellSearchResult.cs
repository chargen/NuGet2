﻿using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuGet.Client.Resources
{
    public class PowershellSearchResult
    {
        public string Id { get; set; }
        public NuGetVersion Version { get; set; }
        public string Summary { get; set; }
    }
}