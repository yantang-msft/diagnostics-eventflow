// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text;
using Xunit;
using Microsoft.Diagnostics.EventFlow.Inputs.Prometheus;
using System.IO;
using System.Linq;
using Io.Prometheus.Client;

namespace Microsoft.Diagnostics.EventFlow.Inputs.Tests
{
    public class PrometheusInputTest
    {
        [Fact]
        public void ParseExampleInput()
        {
            // This is the example from Prometheus website https://prometheus.io/docs/instrumenting/exposition_formats/
            var input =
"# HELP http_requests_total The total number of HTTP requests.                                                                 \n" +
"# TYPE http_requests_total counter                                                                                            \n" +
"http_requests_total{ method = \"post\",code = \"200\"} 1027 1395066363000                                                     \n" +
"http_requests_total{ method = \"post\",code = \"400\"} 3 1395066363000                                                        \n" +
"                                                                                                                              \n" +
"# Escaping in label values:                                                                                                   \n" +
"msdos_file_access_time_seconds{ path = \"C:\\\\DIR\\\\FILE.TXT\",error = \"Cannot find file:\\n\\\"FILE.TXT\\\"\"} 1.458255915e9  \n" +
"                                                                                                                              \n" +
"# Minimalistic line:                                                                                                          \n" +
"metric_without_timestamp_and_labels 12.47                                                                                     \n" +
"                                                                                                                              \n" +
"# A weird metric from before the epoch:                                                                                       \n" +
"something_weird{ problem = \"division by zero\"} +Inf -3982045                                                                \n" +
"                                                                                                                              \n" +
"# A histogram, which has a pretty complex representation in the text format:                                                  \n" +
"# HELP http_request_duration_seconds A histogram of the request duration.                                                     \n" +
"# TYPE http_request_duration_seconds histogram                                                                                \n" +
"http_request_duration_seconds_bucket{ le = \"0.05\"} 24054                                                                    \n" +
"http_request_duration_seconds_bucket{ le = \"0.1\"}  33444                                                                    \n" +
"http_request_duration_seconds_bucket{ le = \"0.2\"}  100392                                                                   \n" +
"http_request_duration_seconds_bucket{ le = \"0.5\"}  129389                                                                   \n" +
"http_request_duration_seconds_bucket{ le = \"1\"}    133988                                                                   \n" +
"http_request_duration_seconds_bucket{ le = \"+Inf\"} 144320                                                                   \n" +
"http_request_duration_seconds_sum 53423                                                                                       \n" +
"http_request_duration_seconds_count 144320                                                                                    \n" +
"                                                                                                                              \n" +
"# Finally a summary, which has a complex representation, too:                                                                 \n" +
"# HELP rpc_duration_seconds A summary of the RPC duration in seconds.                                                         \n" +
"# TYPE rpc_duration_seconds summary                                                                                           \n" +
"rpc_duration_seconds{ quantile = \"0.01\"} 3102                                                                               \n" +
"rpc_duration_seconds{ quantile = \"0.05\"} 3272                                                                               \n" +
"rpc_duration_seconds{ quantile = \"0.5\"}  4773                                                                               \n" +
"rpc_duration_seconds{ quantile = \"0.9\"}  9001                                                                               \n" +
"rpc_duration_seconds{ quantile = \"0.99\"} 76656                                                                              \n" +
"rpc_duration_seconds_sum 1.7560473e+07                                                                                        \n" +
"rpc_duration_seconds_count 2693                                                                                               \n";

            byte[] byteArray = Encoding.UTF8.GetBytes(input);
            var parser = new TextParser(new MemoryStream(byteArray));
            var metricFamilies = parser.GetMetricFamilies().ToList();
            Metric metric = null;

            Assert.True(metricFamilies.Count == 6);

            // Verify http_requests_total
            Assert.True(metricFamilies[0].Name == "http_requests_total");
            Assert.True(metricFamilies[0].Type == MetricType.Counter);
            Assert.True(metricFamilies[0].Help.StartsWith("The total number of HTTP requests."));
            Assert.True(metricFamilies[0].Metric.Count == 2);
            metric = metricFamilies[0].Metric[0];
            Assert.True(metric.Label.Count == 2);
            Assert.True(metric.Label[0].Name == "method" && metric.Label[0].Value == "post");
            Assert.True(metric.Label[1].Name == "code" && metric.Label[1].Value == "200");
            Assert.True(metric.Counter.Value == 1027);
            Assert.True(metric.TimestampMs == 1395066363000);
            metric = metricFamilies[0].Metric[1];
            Assert.True(metric.Label[0].Name == "method" && metric.Label[0].Value == "post");
            Assert.True(metric.Label[1].Name == "code" && metric.Label[1].Value == "400");
            Assert.True(metric.Counter.Value == 3);
            Assert.True(metric.TimestampMs == 1395066363000);

            // Verify msdos_file_access_time_seconds
            Assert.True(metricFamilies[1].Name == "msdos_file_access_time_seconds");
            Assert.True(metricFamilies[1].Type == MetricType.Untyped);
            Assert.True(metricFamilies[1].Metric.Count == 1);
            metric = metricFamilies[1].Metric[0];
            Assert.True(metric.Label.Count == 2);
            Assert.True(metric.Label[0].Name == "path" && metric.Label[0].Value == @"C:\DIR\FILE.TXT");
            Assert.True(metric.Label[1].Name == "error" && metric.Label[1].Value == "Cannot find file:\n\"FILE.TXT\"");
            Assert.True(metric.Untyped.Value == 1458255915);

            // Verify metric_without_timestamp_and_labels
            Assert.True(metricFamilies[2].Name == "metric_without_timestamp_and_labels");
            Assert.True(metricFamilies[2].Type == MetricType.Untyped);
            Assert.True(metricFamilies[2].Metric.Count == 1);
            metric = metricFamilies[2].Metric[0];
            Assert.True(metric.Label.Count == 0);
            Assert.True(metric.Untyped.Value == 12.47);

            // Verify something_weird
            Assert.True(metricFamilies[3].Name == "something_weird");
            Assert.True(metricFamilies[3].Type == MetricType.Untyped);
            Assert.True(metricFamilies[3].Metric.Count == 1);
            metric = metricFamilies[3].Metric[0];
            Assert.True(metric.Label.Count == 1);
            Assert.True(metric.Label[0].Name == "problem" && metric.Label[0].Value == "division by zero");
            Assert.True(metric.Untyped.Value == double.PositiveInfinity);
            Assert.True(metric.TimestampMs == -3982045);

            // Verify http_request_duration_seconds
            Assert.True(metricFamilies[4].Name == "http_request_duration_seconds");
            Assert.True(metricFamilies[4].Type == MetricType.Histogram);
            Assert.True(metricFamilies[4].Help.StartsWith("A histogram of the request duration."));
            Assert.True(metricFamilies[4].Metric.Count == 1);
            metric = metricFamilies[4].Metric[0];
            Assert.True(metric.Label.Count == 0);
            Assert.True(metric.Histogram.SampleSum == 53423);
            Assert.True(metric.Histogram.SampleCount == 144320);
            Assert.True(metric.Histogram.Bucket.Count == 6);
            Assert.True(metric.Histogram.Bucket[0].UpperBound == 0.05 && metric.Histogram.Bucket[0].CumulativeCount == 24054);
            Assert.True(metric.Histogram.Bucket[1].UpperBound == 0.1 && metric.Histogram.Bucket[1].CumulativeCount == 33444);
            Assert.True(metric.Histogram.Bucket[2].UpperBound == 0.2 && metric.Histogram.Bucket[2].CumulativeCount == 100392);
            Assert.True(metric.Histogram.Bucket[3].UpperBound == 0.5 && metric.Histogram.Bucket[3].CumulativeCount == 129389);
            Assert.True(metric.Histogram.Bucket[4].UpperBound == 1 && metric.Histogram.Bucket[4].CumulativeCount == 133988);
            Assert.True(metric.Histogram.Bucket[5].UpperBound == double.PositiveInfinity && metric.Histogram.Bucket[5].CumulativeCount == 144320);

            // Verify http_request_duration_seconds
            Assert.True(metricFamilies[5].Name == "rpc_duration_seconds");
            Assert.True(metricFamilies[5].Type == MetricType.Summary);
            Assert.True(metricFamilies[5].Help.StartsWith("A summary of the RPC duration in seconds."));
            Assert.True(metricFamilies[5].Metric.Count == 1);
            metric = metricFamilies[5].Metric[0];
            Assert.True(metric.Label.Count == 0);
            Assert.True(metric.Summary.SampleSum == 17560473);
            Assert.True(metric.Summary.SampleCount == 2693);
            Assert.True(metric.Summary.Quantile.Count == 5);
            Assert.True(metric.Summary.Quantile[0].Quantile_ == 0.01 && metric.Summary.Quantile[0].Value == 3102);
            Assert.True(metric.Summary.Quantile[1].Quantile_ == 0.05 && metric.Summary.Quantile[1].Value == 3272);
            Assert.True(metric.Summary.Quantile[2].Quantile_ == 0.5 && metric.Summary.Quantile[2].Value == 4773);
            Assert.True(metric.Summary.Quantile[3].Quantile_ == 0.9 && metric.Summary.Quantile[3].Value == 9001);
            Assert.True(metric.Summary.Quantile[4].Quantile_ == 0.99 && metric.Summary.Quantile[4].Value == 76656);
        }
    }
}
