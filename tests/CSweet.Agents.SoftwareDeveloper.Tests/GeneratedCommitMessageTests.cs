namespace CSweet.Agents.SoftwareDeveloper.Tests;

public sealed class GeneratedCommitMessageTests
{
    [Fact]
    public void Generated_subject_is_conventional_single_line_and_bounded()
    {
        Assert.Equal("feat(task): Scaffold build", GeneratedCommitMessage.Create(" Scaffold\r\n build "));
        Assert.Equal(512, GeneratedCommitMessage.Create(new string('x', 1000)).Length);
        Assert.Throws<ArgumentException>(() => GeneratedCommitMessage.Create(" \n\t"));
    }
}
