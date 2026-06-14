using Rig.Domain;
using Shouldly;

namespace Rig.Tests.Analysis;

public sealed class ProjectContentHashTests
{
    [Test]
    public void Same_inputs_hash_the_same()
    {
        var a = ProjectContentHash.Compute(["class A {}", "class B {}"]);
        var b = ProjectContentHash.Compute(["class A {}", "class B {}"]);
        a.ShouldBe(b);
    }

    [Test]
    public void Order_of_files_does_not_change_the_hash()
    {
        var forward = ProjectContentHash.Compute(["class A {}", "class B {}", "class C {}"]);
        var shuffled = ProjectContentHash.Compute(["class C {}", "class A {}", "class B {}"]);
        shuffled.ShouldBe(forward);
    }

    [Test]
    public void Editing_a_file_changes_the_hash()
    {
        var before = ProjectContentHash.Compute(["class A {}", "class B {}"]);
        var after = ProjectContentHash.Compute(["class A {}", "class B { int x; }"]);
        after.ShouldNotBe(before);
    }

    [Test]
    public void Adding_a_file_changes_the_hash()
    {
        var before = ProjectContentHash.Compute(["class A {}"]);
        var after = ProjectContentHash.Compute(["class A {}", "class B {}"]);
        after.ShouldNotBe(before);
    }

    [Test]
    public void Empty_input_is_stable_and_nonempty()
    {
        var first = ProjectContentHash.Compute([]);
        var second = ProjectContentHash.Compute([]);
        first.ShouldBe(second);
        first.ShouldNotBeNullOrEmpty();
    }
}
