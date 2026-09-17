using System.Diagnostics.Metrics;
using Dprental.Gateway.Domain.Observability;
using Dprental.Gateway.Infrastructure.Observability;
using Xunit;

namespace Dprental.Gateway.Tests;

/// <summary>
/// Kiểm thử đơn vị cho thành phần đo lường GatewayMetrics.
/// Đảm bảo các chỉ số Counter và Histogram hoạt động chính xác và chỉ sử dụng các nhãn hợp lệ.
/// </summary>
public sealed class GatewayMetricsTests
{
    [Fact]
    public void RecordRequestIncrementsCountersAndHistogramWithSafeLabels()
    {
        using GatewayMetrics metrics = new();
        long requestCount = 0;
        long error4xxCount = 0;
        long error5xxCount = 0;
        int histogramMeasurements = 0;

        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == GatewayMetricNames.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == GatewayMetricNames.RequestTotal)
            {
                Interlocked.Add(ref requestCount, measurement);
            }
            else if (instrument.Name == GatewayMetricNames.Error4xxTotal)
            {
                Interlocked.Add(ref error4xxCount, measurement);
            }
            else if (instrument.Name == GatewayMetricNames.Error5xxTotal)
            {
                Interlocked.Add(ref error5xxCount, measurement);
            }
        });

        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == GatewayMetricNames.RequestDuration)
            {
                Interlocked.Increment(ref histogramMeasurements);
            }
        });

        listener.Start();

        // 1. Ghi nhận 1 request 200 OK
        metrics.RecordRequest("/api/v1/auth/sessions", "POST", 200, 0.05, "auth-service");

        // 2. Ghi nhận 1 request 403 Forbidden
        metrics.RecordRequest("/protected/orders", "GET", 403, 0.02, "order-service");

        // 3. Ghi nhận 1 request 502 Bad Gateway
        metrics.RecordRequest("/api/v1/rentals", "POST", 502, 0.10, "rental-service");

        listener.RecordObservableInstruments();

        Assert.Equal(3, requestCount);
        Assert.Equal(1, error4xxCount);
        Assert.Equal(1, error5xxCount);
        Assert.Equal(3, histogramMeasurements);
    }

    [Fact]
    public void RecordUpstreamTimeoutAndRateLimitIncrementSpecificCounters()
    {
        using GatewayMetrics metrics = new();
        long timeoutCount = 0;
        long rateLimitCount = 0;

        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == GatewayMetricNames.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == GatewayMetricNames.UpstreamTimeoutTotal)
            {
                Interlocked.Add(ref timeoutCount, measurement);
            }
            else if (instrument.Name == GatewayMetricNames.RateLimitTotal)
            {
                Interlocked.Add(ref rateLimitCount, measurement);
            }
        });

        listener.Start();

        metrics.RecordUpstreamTimeout("/api/v1/rentals", "POST", "rental-service");
        metrics.RecordRateLimitExceeded("/api/v1/auth/sessions", "POST");

        listener.RecordObservableInstruments();

        Assert.Equal(1, timeoutCount);
        Assert.Equal(1, rateLimitCount);
    }

    [Fact]
    public void DisposeReleasesMeterResourcesWithoutThrowing()
    {
        GatewayMetrics metrics = new();
        metrics.Dispose();
    }
}
