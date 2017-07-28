// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Io.Prometheus.Client;
using Microsoft.Diagnostics.EventFlow.Configuration;
using Microsoft.Diagnostics.EventFlow.Inputs.Prometheus;
using Microsoft.Diagnostics.EventFlow.Metadata;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Validation;

namespace Microsoft.Diagnostics.EventFlow.Inputs
{
    public class PrometheusInput : IObservable<EventData>, IDisposable
    {
        public static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0);
        private EventFlowSubject<EventData> subject;
        private IHealthReporter healthReporter;
        private bool disposed = false;
        private CancellationTokenSource cancellationTokenSource;

        // The summary and histogram metric is aggregated since the start of a program.
        // To get the aggregated value during the scrape interval period, we need to remember the last metric and calculate the difference.
        private ConcurrentDictionary<string, Metric> lastSummaryMetricDict;
        private ConcurrentDictionary<string, Metric> lastHistogramMetricDict;

        public PrometheusInput(IConfiguration configuration, IHealthReporter healthReporter)
        {
            Requires.NotNull(configuration, nameof(configuration));
            Requires.NotNull(healthReporter, nameof(healthReporter));

            var inputConfiguration = new PrometheusInputConfiguration();
            try
            {
                configuration.Bind(inputConfiguration);
            }
            catch (Exception e)
            {
                healthReporter.ReportProblem($"{nameof(PrometheusInput)}: an error occurred when reading configuration{Environment.NewLine}{e.ToString()}");
                return;
            }

            Initialize(inputConfiguration, healthReporter);
        }

        public void Dispose()
        {
            if (!this.disposed)
            {
                lock (this.subject)
                {
                    if (!this.disposed)
                    {
                        this.disposed = true;
                        this.cancellationTokenSource.Cancel();
                        this.subject.Dispose();
                    }
                }
            }
        }

        public IDisposable Subscribe(IObserver<EventData> observer)
        {
            return this.subject.Subscribe(observer);
        }

        private void Initialize(PrometheusInputConfiguration configuration, IHealthReporter healthReporter)
        {
            this.healthReporter = healthReporter;
            this.subject = new EventFlowSubject<EventData>();
            this.cancellationTokenSource = new CancellationTokenSource();
            this.lastHistogramMetricDict = new ConcurrentDictionary<string, Metric>();
            this.lastSummaryMetricDict = new ConcurrentDictionary<string, Metric>();

            foreach (var url in configuration.Urls)
            {
                Task.Run(async () =>
                {
                    while (!this.cancellationTokenSource.IsCancellationRequested)
                    {
                        var nextStartTime = DateTime.Now.AddMilliseconds(configuration.ScrapeIntervalMsec);
                        await GetMetricFromUrl(url);
                        var endTime = DateTime.Now;

                        if (endTime < nextStartTime)
                        {
                            await Task.Delay(nextStartTime - endTime);
                        }
                    }
                });
            };
        }

        private async Task GetMetricFromUrl(string url)
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.google.protobuf;proto=io.prometheus.client.MetricFamily;encoding=delimited;q=0.7,text/plain;version=0.0.4;q=0.3");
            //client.DefaultRequestHeaders.Add("Accept", "text/plain;version=0.0.4;q=0.3");
            // TODO: Add authentication

            var requestTime = DateTimeOffset.UtcNow;
            var response = await client.GetAsync(url);
            var stream = await response.Content.ReadAsStreamAsync();
            if (response.Content.Headers.ContentType.MediaType == "application/vnd.google.protobuf")
            {
                while (stream.Position < stream.Length)
                {
                    MetricFamily mf = MetricFamily.Parser.ParseDelimitedFrom(stream);
                    foreach( var eventData in ConvertToEventData(mf, url, requestTime))
                    {
                        this.subject.OnNext(eventData);
                    }
                }
            }
            else
            {
                var parser = new TextParser(stream);
                foreach(var mf in parser.GetMetricFamilies())
                {
                    foreach (var eventData in ConvertToEventData(mf, url, requestTime))
                    {
                        this.subject.OnNext(eventData);
                    }
                }
            }
        }

        private IEnumerable<EventData> ConvertToEventData(MetricFamily mf, string url, DateTimeOffset requestTime)
        {
            var result = new List<EventData>();

            foreach (var metric in mf.Metric)
            {
                var data = new EventData()
                {
                    ProviderName = url,
                    Timestamp = metric.TimestampMs != 0 ? PrometheusInput.Epoch.AddMilliseconds(metric.TimestampMs) : requestTime,
                };

                data.Payload["Type"] = mf.Type.ToString();
                foreach (var label in metric.Label)
                {
                    data.Payload["label_" + label.Name] = label.Value;
                }

                // Special handling on different metric types
                EventMetadata metricMetadata = null;
                switch (mf.Type)
                {
                    case MetricType.Counter:
                        metricMetadata = GetSingleValueMetricMetadata(mf.Name, metric.Counter.Value.ToString());
                        break;
                    case MetricType.Gauge:
                        metricMetadata = GetSingleValueMetricMetadata(mf.Name, metric.Gauge.Value.ToString());
                        break;
                    case MetricType.Untyped:
                        metricMetadata = GetSingleValueMetricMetadata(mf.Name, metric.Untyped.Value.ToString());
                        break;
                    case MetricType.Histogram:
                        metricMetadata = GetHistogramMetricMetadata(url, mf.Name, metric);
                        foreach (var bucket in metric.Histogram.Bucket)
                        {
                            data.Payload["bucket_" + bucket.UpperBound] = bucket.CumulativeCount;
                        }
                        break;
                    case MetricType.Summary:
                        metricMetadata = GetSummaryMetricMetadata(url, mf.Name, metric);
                        foreach (var quantile in metric.Summary.Quantile)
                        {
                            data.Payload["quantile_" + quantile.Quantile_] = quantile.Value;
                        }
                        break;
                }

                // First sample of Histogram and Summary metric will be ignored
                if (metricMetadata != null)
                {
                    data.SetMetadata(metricMetadata);
                    result.Add(data);
                }
            }

            return result;
        }

        private EventMetadata GetSingleValueMetricMetadata(string metricName, string metricValue)
        {
            var metricMetadata = new EventMetadata(MetricData.MetricMetadataKind);
            metricMetadata.Properties[MetricData.MetricNameMoniker] = metricName;
            metricMetadata.Properties[MetricData.MetricValueMoniker] = metricValue;

            return metricMetadata;
        }

        private EventMetadata GetHistogramMetricMetadata(string url, string metricName, Metric metric)
        {
            var key = GenerateKeyForMetricDict(url, metricName, metric);
            if (!lastHistogramMetricDict.ContainsKey(key))
            {
                // This is the first sample, just ignore
                lastHistogramMetricDict[key] = metric;
                return null;
            }
            else
            {
                var lastMetric = lastHistogramMetricDict[key];
                var metricMetadata = new EventMetadata(AggregatedMetricData.MetricMetadataKind);
                metricMetadata.Properties[AggregatedMetricData.MetricNameMoniker] = metricName;
                metricMetadata.Properties[AggregatedMetricData.MetricSumMoniker] = (metric.Histogram.SampleSum - lastMetric.Histogram.SampleSum).ToString();
                metricMetadata.Properties[AggregatedMetricData.MetricCountMoniker] = (metric.Histogram.SampleCount - lastMetric.Histogram.SampleCount).ToString();

                lastHistogramMetricDict[key] = metric;

                return metricMetadata;
            }
        }

        private EventMetadata GetSummaryMetricMetadata(string url, string metricName, Metric metric)
        {
            var key = GenerateKeyForMetricDict(url, metricName, metric);
            if (!lastSummaryMetricDict.ContainsKey(key))
            {
                // This is the first sample, just ignore
                lastSummaryMetricDict[key] = metric;
                return null;
            }
            else
            {
                var lastMetric = lastSummaryMetricDict[key];
                var metricMetadata = new EventMetadata(AggregatedMetricData.MetricMetadataKind);
                metricMetadata.Properties[AggregatedMetricData.MetricNameMoniker] = metricName;
                metricMetadata.Properties[AggregatedMetricData.MetricSumMoniker] = (metric.Summary.SampleSum - lastMetric.Summary.SampleSum).ToString();
                metricMetadata.Properties[AggregatedMetricData.MetricCountMoniker] = (metric.Summary.SampleCount - lastMetric.Summary.SampleCount).ToString();

                lastSummaryMetricDict[key] = metric;

                return metricMetadata;
            }
        }

        private string GenerateKeyForMetricDict(string url, string metricName, Metric metric)
        {
            var sb = new StringBuilder();
            sb.Append(url);
            sb.Append(";" + metricName);

            var orderedLabel = metric.Label.OrderBy(label => label.Name);
            foreach (var label in orderedLabel)
            {
                sb.Append(";" + label.Name + ":" + label.Value);
            }

            return sb.ToString();
        }
    }
}
