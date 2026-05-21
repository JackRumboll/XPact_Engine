// Copyright Simgenics. All Rights Reserved.

using System;
using System.IO;
using Simgenics.XPact.XHT.Core;
using Xunit;

namespace Simgenics.XPact.XHT.Tests.Tests.Core;

/// <summary>
/// Tests for <see cref="FileSystemOps.RetryOnTransientIOException"/>.
/// The retry schedule is the four-attempt backoff
/// <c>[100ms, 200ms, 1000ms, 5000ms]</c>; we don't actually want to
/// burn 6.3 s in the test suite, so the failing-eventually test triggers
/// the schedule's exhaustion only when explicitly exercised (one test).
/// </summary>
public class FileSystemOpsTests
{
    [Fact]
    public void Operation_Succeeds_OnFirstTry()
    {
        int callCount = 0;
        FileSystemOps.RetryOnTransientIOException(() => callCount++);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void Operation_SucceedsEventually_AfterTransientIoException()
    {
        int callCount = 0;
        FileSystemOps.RetryOnTransientIOException(() =>
        {
            callCount++;
            if (callCount < 3)
            {
                throw new IOException("transient sharing violation simulation");
            }
            // third attempt succeeds
        });
        Assert.Equal(3, callCount);
    }

    [Fact]
    public void Operation_SucceedsEventually_AfterUnauthorizedAccessException()
    {
        int callCount = 0;
        FileSystemOps.RetryOnTransientIOException(() =>
        {
            callCount++;
            if (callCount < 2)
            {
                throw new UnauthorizedAccessException("AV quarantine simulation");
            }
            // second attempt succeeds
        });
        Assert.Equal(2, callCount);
    }

    [Fact]
    public void NonTransientException_PropagatesImmediately()
    {
        int callCount = 0;
        // ArgumentException is NOT in the retry filter; it should fly
        // through on the first attempt.
        Assert.Throws<ArgumentException>(() =>
        {
            FileSystemOps.RetryOnTransientIOException(() =>
            {
                callCount++;
                throw new ArgumentException("not retryable");
            });
        });
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void NullOperation_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => FileSystemOps.RetryOnTransientIOException(null!));
    }

    /// <summary>
    /// Stress: every retry attempt fails. The schedule exhausts and the
    /// last exception propagates. This is slow (~6.3 s) so it is
    /// deliberately the only one that pays the cost; it is essential to
    /// verify the retry surrender path.
    /// </summary>
    [Fact]
    public void Operation_FailingEveryTime_ExhaustsScheduleAndThrows()
    {
        int callCount = 0;
        Assert.Throws<IOException>(() =>
        {
            FileSystemOps.RetryOnTransientIOException(() =>
            {
                callCount++;
                throw new IOException("persistent failure");
            });
        });
        // 1 initial + 4 retries = 5 attempts total per the schedule.
        Assert.Equal(5, callCount);
    }
}
