// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Validation;

namespace Microsoft.Diagnostics.EventFlow.Metadata
{
    public class AggregatedMetricData
    {
        public static readonly string MetricMetadataKind = "aggregatedMetric";
        public static readonly string MetricNameMoniker = "metricName";
        public static readonly string MetricSumMoniker = "metricSum";
        public static readonly string MetricCountMoniker = "metricCount";

        public string MetricName { get; private set; }
        public double Sum { get; private set; }
        public int Count { get; private set; }

        // Ensure that AggregatedMetricData can only be created using TryGetMetricData() method
        private AggregatedMetricData() { }

        public static DataRetrievalResult TryGetData(EventData eventData, EventMetadata metricMetadata, out AggregatedMetricData metric)
        {
            Requires.NotNull(eventData, nameof(eventData));
            Requires.NotNull(metricMetadata, nameof(metricMetadata));
            metric = null;

            if (!MetricMetadataKind.Equals(metricMetadata.MetadataType, System.StringComparison.OrdinalIgnoreCase))
            {
                return DataRetrievalResult.InvalidMetadataType(metricMetadata.MetadataType, MetricMetadataKind);
            }

            string metricName = metricMetadata[MetricNameMoniker];
            if (string.IsNullOrEmpty(metricName))
            {
                return DataRetrievalResult.MissingMetadataProperty(MetricNameMoniker);
            }

            metric = new AggregatedMetricData();
            metric.MetricName = metricName;

            double sum = default(double);
            string rawSumValue = metricMetadata[MetricSumMoniker];
            if (!string.IsNullOrEmpty(rawSumValue) && !double.TryParse(rawSumValue, out sum))
            {
                return DataRetrievalResult.InvalidMetadataPropertyValue(MetricSumMoniker, rawSumValue);
            }

            int count = default(int);
            string rawCountValue = metricMetadata[MetricCountMoniker];
            if (!string.IsNullOrEmpty(rawCountValue) && !int.TryParse(rawCountValue, out count))
            {
                return DataRetrievalResult.InvalidMetadataPropertyValue(MetricCountMoniker, rawCountValue);
            }

            metric = new AggregatedMetricData();
            metric.MetricName = metricName;
            metric.Sum = sum;
            metric.Count = count;

            return DataRetrievalResult.Success;
        }
    }
}
