// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Diagnostics;

namespace Microsoft.Diagnostics.EventFlow.Configuration
{
    // !!ACTION!!
    // If you make any changes here, please update the README.md file to reflect the new configuration
    public class PrometheusInputConfiguration: ItemConfiguration
    {
        public string[] Urls{ get; set; }
        public int ScrapeIntervalMsec { get; set; }

        public PrometheusInputConfiguration()
        {
            ScrapeIntervalMsec = 5000;
        }
    }
}
