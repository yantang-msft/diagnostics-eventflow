using Io.Prometheus.Client;
using Microsoft.Diagnostics.EventFlow.Configuration;
using Microsoft.Diagnostics.EventFlow.Metadata;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Net.Http;
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
        private Timer timer;

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
                        this.subject.Dispose();
                        timer.Dispose();
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

            this.timer = new Timer(_ =>
            {
                foreach (var url in configuration.Urls)
                {
                    Task.Run(() => GetMetricFromUrl(url));
                }
            }, null, 0, configuration.ScrapeIntervalMsec);
        }

        private async void GetMetricFromUrl(string url)
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.google.protobuf;proto=io.prometheus.client.MetricFamily;encoding=delimited;q=0.7,text/plain;version=0.0.4;q=0.3");
            // TODO: Add authentication

            var requestTime = DateTimeOffset.UtcNow;
            var response = await client.GetAsync(url);
            if (response.Content.Headers.ContentType.MediaType == "application/vnd.google.protobuf")
            {
                var stream = await response.Content.ReadAsStreamAsync();
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
                // TODO: parse plain text format
            }

            return;
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

                // General metric properties: Name, Type, Labels
                var metricMetadata = new EventMetadata(MetricData.MetricMetadataKind);
                metricMetadata.Properties[MetricData.MetricNameMoniker] = mf.Name;

                data.Payload["Type"] = mf.Type.ToString();
                foreach (var label in metric.Label)
                {
                    data.Payload["label_" + label.Name] = label.Value;
                }

                // Special handling on different metric types
                switch (mf.Type)
                {
                    case MetricType.Counter:
                        metricMetadata.Properties[MetricData.MetricValueMoniker] = metric.Counter.Value.ToString();
                        break;
                    case MetricType.Gauge:
                        metricMetadata.Properties[MetricData.MetricValueMoniker] = metric.Gauge.Value.ToString();
                        break;
                    case MetricType.Untyped:
                        metricMetadata.Properties[MetricData.MetricValueMoniker] = metric.Untyped.Value.ToString();
                        break;
                    case MetricType.Histogram:  // TODO: Support aggregation in metric metadata
                        metricMetadata.Properties[MetricData.MetricValueMoniker] = "0";
                        data.Payload["Count"] = metric.Histogram.SampleCount;
                        data.Payload["Sum"] = metric.Histogram.SampleSum;
                        foreach (var bucket in metric.Histogram.Bucket)
                        {
                            data.Payload["bucket_" + bucket.UpperBound] = bucket.CumulativeCount;
                        }
                        break;
                    case MetricType.Summary:
                        metricMetadata.Properties[MetricData.MetricValueMoniker] = "0";
                        data.Payload["Count"] = metric.Summary.SampleCount;
                        data.Payload["Sum"] = metric.Summary.SampleSum;
                        foreach (var quantile in metric.Summary.Quantile)
                        {
                            data.Payload["quantile_" + quantile.Quantile_] = quantile.Value;
                        }
                        break;
                }
                data.SetMetadata(metricMetadata);

                result.Add(data);
            }

            return result;
        }
    }
}
