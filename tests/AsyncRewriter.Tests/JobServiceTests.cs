using AsyncRewriter.Server.Models;
using AsyncRewriter.Server.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AsyncRewriter.Tests;

public class JobServiceTests
{
    private readonly JobService _jobService;
    private readonly Mock<ILogger<JobService>> _mockLogger;

    public JobServiceTests()
    {
        _mockLogger = new Mock<ILogger<JobService>>();
        _jobService = new JobService(_mockLogger.Object);
    }

    [Fact]
    public void CreateJob_ValidProjectPath_ReturnsJobId()
    {
        // Arrange
        var projectPath = "/path/to/project.csproj";

        // Act
        var jobId = _jobService.CreateJob(projectPath);

        // Assert
        jobId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void CreateJob_ValidProjectPath_CreatesJobWithQueuedStatus()
    {
        // Arrange
        var projectPath = "/path/to/project.csproj";

        // Act
        var jobId = _jobService.CreateJob(projectPath);
        var job = _jobService.GetJob(jobId);

        // Assert
        job.Should().NotBeNull();
        job!.Status.Should().Be(JobStatus.Queued);
        job.ProjectPath.Should().Be(projectPath);
    }

    [Fact]
    public void CreateJob_ValidProjectPath_SetsCreatedAt()
    {
        // Arrange
        var projectPath = "/path/to/project.csproj";
        var beforeCreate = DateTime.UtcNow;

        // Act
        var jobId = _jobService.CreateJob(projectPath);
        var job = _jobService.GetJob(jobId);
        var afterCreate = DateTime.UtcNow;

        // Assert
        job.Should().NotBeNull();
        job!.CreatedAt.Should().BeOnOrAfter(beforeCreate);
        job.CreatedAt.Should().BeOnOrBefore(afterCreate);
    }

    [Fact]
    public void GetJob_ExistingJobId_ReturnsJob()
    {
        // Arrange
        var projectPath = "/path/to/project.csproj";
        var jobId = _jobService.CreateJob(projectPath);

        // Act
        var job = _jobService.GetJob(jobId);

        // Assert
        job.Should().NotBeNull();
        job!.JobId.Should().Be(jobId);
    }

    [Fact]
    public void GetJob_NonExistentJobId_ReturnsNull()
    {
        // Act
        var job = _jobService.GetJob("non-existent-job-id");

        // Assert
        job.Should().BeNull();
    }

    [Fact]
    public void UpdateJob_ExistingJob_UpdatesJobProperties()
    {
        // Arrange
        var projectPath = "/path/to/project.csproj";
        var jobId = _jobService.CreateJob(projectPath);

        // Act
        _jobService.UpdateJob(jobId, job =>
        {
            job.Status = JobStatus.Processing;
            job.ProgressPercentage = 50;
        });

        var updatedJob = _jobService.GetJob(jobId);

        // Assert
        updatedJob.Should().NotBeNull();
        updatedJob!.Status.Should().Be(JobStatus.Processing);
        updatedJob.ProgressPercentage.Should().Be(50);
    }

    [Fact]
    public void UpdateJob_NonExistentJob_DoesNotThrow()
    {
        // Act & Assert
        var act = () => _jobService.UpdateJob("non-existent-job-id", job => job.Status = JobStatus.Processing);
        act.Should().NotThrow();
    }

    [Fact]
    public void GetQueuedJobs_MultipleJobsQueued_ReturnsAllQueuedJobs()
    {
        // Arrange
        var jobId1 = _jobService.CreateJob("/path/to/project1.csproj");
        var jobId2 = _jobService.CreateJob("/path/to/project2.csproj");
        var jobId3 = _jobService.CreateJob("/path/to/project3.csproj");

        // Act
        var queuedJobs = _jobService.GetQueuedJobs().ToList();

        // Assert
        queuedJobs.Should().HaveCount(3);
        queuedJobs.Select(j => j.JobId).Should().Contain(new[] { jobId1, jobId2, jobId3 });
    }

    [Fact]
    public void GetQueuedJobs_NoJobsQueued_ReturnsEmptyList()
    {
        // Act
        var queuedJobs = _jobService.GetQueuedJobs().ToList();

        // Assert
        queuedJobs.Should().BeEmpty();
    }

    [Fact]
    public void GetQueuedJobs_CalledMultipleTimes_DepletesQueue()
    {
        // Arrange
        _jobService.CreateJob("/path/to/project1.csproj");
        _jobService.CreateJob("/path/to/project2.csproj");

        // Act
        var firstCall = _jobService.GetQueuedJobs().ToList();
        var secondCall = _jobService.GetQueuedJobs().ToList();

        // Assert
        firstCall.Should().HaveCount(2);
        secondCall.Should().BeEmpty();
    }

    [Fact]
    public void CancelJob_QueuedJob_CancelsAndReturnsTrue()
    {
        // Arrange
        var jobId = _jobService.CreateJob("/path/to/project.csproj");

        // Act
        var result = _jobService.CancelJob(jobId);
        var job = _jobService.GetJob(jobId);

        // Assert
        result.Should().BeTrue();
        job.Should().NotBeNull();
        job!.Status.Should().Be(JobStatus.Cancelled);
        job.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void CancelJob_ProcessingJob_CancelsAndReturnsTrue()
    {
        // Arrange
        var jobId = _jobService.CreateJob("/path/to/project.csproj");
        _jobService.UpdateJob(jobId, job => job.Status = JobStatus.Processing);

        // Act
        var result = _jobService.CancelJob(jobId);
        var job = _jobService.GetJob(jobId);

        // Assert
        result.Should().BeTrue();
        job.Should().NotBeNull();
        job!.Status.Should().Be(JobStatus.Cancelled);
    }

    [Fact]
    public void CancelJob_CompletedJob_DoesNotCancelAndReturnsFalse()
    {
        // Arrange
        var jobId = _jobService.CreateJob("/path/to/project.csproj");
        _jobService.UpdateJob(jobId, job => job.Status = JobStatus.Completed);

        // Act
        var result = _jobService.CancelJob(jobId);
        var job = _jobService.GetJob(jobId);

        // Assert
        result.Should().BeFalse();
        job.Should().NotBeNull();
        job!.Status.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public void CancelJob_FailedJob_DoesNotCancelAndReturnsFalse()
    {
        // Arrange
        var jobId = _jobService.CreateJob("/path/to/project.csproj");
        _jobService.UpdateJob(jobId, job => job.Status = JobStatus.Failed);

        // Act
        var result = _jobService.CancelJob(jobId);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void CancelJob_NonExistentJob_ReturnsFalse()
    {
        // Act
        var result = _jobService.CancelJob("non-existent-job-id");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void CancelJob_CancelsToken()
    {
        // Arrange
        var jobId = _jobService.CreateJob("/path/to/project.csproj");
        var job = _jobService.GetJob(jobId);

        // Act
        _jobService.CancelJob(jobId);

        // Assert
        job.Should().NotBeNull();
        job!.CancellationTokenSource.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void CreateJob_MultipleJobs_GeneratesUniqueJobIds()
    {
        // Arrange & Act
        var jobId1 = _jobService.CreateJob("/path/to/project1.csproj");
        var jobId2 = _jobService.CreateJob("/path/to/project2.csproj");
        var jobId3 = _jobService.CreateJob("/path/to/project3.csproj");

        // Assert
        jobId1.Should().NotBe(jobId2);
        jobId2.Should().NotBe(jobId3);
        jobId1.Should().NotBe(jobId3);
    }

    [Fact]
    public void GetQueuedJobs_ReturnsJobsInFifoOrder()
    {
        // Arrange
        var jobId1 = _jobService.CreateJob("/path/to/project1.csproj");
        var jobId2 = _jobService.CreateJob("/path/to/project2.csproj");
        var jobId3 = _jobService.CreateJob("/path/to/project3.csproj");

        // Act
        var queuedJobs = _jobService.GetQueuedJobs().ToList();

        // Assert
        queuedJobs[0].JobId.Should().Be(jobId1);
        queuedJobs[1].JobId.Should().Be(jobId2);
        queuedJobs[2].JobId.Should().Be(jobId3);
    }
}
