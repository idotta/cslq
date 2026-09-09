namespace Cslq.Tests;

/// <summary>
/// <see cref="ProjectSources.Read"/> is what keeps readiness from probing a project for
/// sources it does not compile. It is pure text so that these cases can be one string each:
/// the trees that exercise the same shapes end-to-end are in
/// <see cref="SentinelInferenceTests"/>, and neither shape can be reached from
/// <c>probes/cases.jsonl</c>, whose fixtures are all ordinary SDK projects.
/// </summary>
public class ProjectSourcesTests
{
    [Fact]
    public void An_ordinary_project_compiles_its_own_directory()
    {
        var kind = ProjectSources.Read("<Project Sdk=\"Microsoft.NET.Sdk\" />");

        Assert.False(kind.Elsewhere);
        Assert.False(kind.None);
    }

    /// <summary>
    /// OrchardCore's <c>OrchardCore.ProjectTemplates</c>, reduced. Its <c>&lt;None
    /// Include="content\**"&gt;</c> is a pack item, not a compile item, so it does not count
    /// as a source of its own.
    /// </summary>
    [Fact]
    public void Default_items_off_with_no_compile_include_compiles_nothing()
    {
        var kind = ProjectSources.Read("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <EnableDefaultItems>False</EnableDefaultItems>
              </PropertyGroup>
              <ItemGroup>
                <None Include="content\**" Pack="true" />
              </ItemGroup>
            </Project>
            """);

        Assert.True(kind.None);
        Assert.True(kind.Elsewhere);
    }

    /// <summary>
    /// A conditioned <c>false</c> is unknown, not true: this project's Debug build — the one
    /// the server loads — keeps the default glob and compiles files of its own, so skipping it
    /// would be a wrong answer at exit 0.
    /// </summary>
    [Fact]
    public void A_conditioned_default_items_off_leaves_the_project_ordinary()
    {
        var kind = ProjectSources.Read("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup Condition="'$(Configuration)'=='Release'">
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
            </Project>
            """);

        Assert.False(kind.None);
        Assert.False(kind.Elsewhere);
    }

    /// <summary>
    /// The unconditional value still decides. A conditioned <c>true</c> beside it says nothing
    /// this can evaluate, and ignoring the conditioned element is not the same as letting it
    /// override the one that always applies.
    /// </summary>
    [Fact]
    public void An_unconditional_false_beside_a_conditioned_true_still_compiles_nothing()
    {
        var kind = ProjectSources.Read("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <EnableDefaultItems>false</EnableDefaultItems>
              </PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)'=='Debug'">
                <EnableDefaultItems>true</EnableDefaultItems>
              </PropertyGroup>
            </Project>
            """);

        Assert.True(kind.None);
        Assert.True(kind.Elsewhere);
    }

    /// <summary>
    /// Default items off and every include pointing outside is the same "compiles nothing of
    /// its own" as an empty include list, and it has to be, or the candidate scan reads types
    /// out of files MSBuild never compiles — the ProjectTemplates failure by another route.
    /// </summary>
    [Fact]
    public void Default_items_off_with_only_outside_includes_compiles_nothing_of_its_own()
    {
        var kind = ProjectSources.Read("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <EnableDefaultItems>false</EnableDefaultItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="..\Shared\**\*.cs" />
              </ItemGroup>
            </Project>
            """);

        Assert.True(kind.None);
        Assert.True(kind.Elsewhere);
    }

    [Fact]
    public void Default_items_off_with_a_compile_include_of_its_own_is_ordinary()
    {
        var kind = ProjectSources.Read("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Real.cs" />
              </ItemGroup>
            </Project>
            """);

        Assert.False(kind.None);
        Assert.False(kind.Elsewhere);
    }

    /// <summary>
    /// CommunityToolkit's <c>CommunityToolkit.Mvvm.CodeFixers.Roslyn4001</c>, verbatim. The
    /// default glob is still on, so whether this project has sources of its own is a question
    /// about the disk — <c>Elsewhere</c> alone is not enough to skip it.
    /// </summary>
    [Fact]
    public void A_projitems_import_puts_the_sources_elsewhere()
    {
        var kind = ProjectSources.Read("""
            <Project Sdk="Microsoft.NET.Sdk">

              <Import Project="..\CommunityToolkit.Mvvm.CodeFixers\CommunityToolkit.Mvvm.CodeFixers.props" />
              <Import Project="..\CommunityToolkit.Mvvm.CodeFixers\CommunityToolkit.Mvvm.CodeFixers.projitems" Label="Shared" />

            </Project>
            """);

        Assert.True(kind.Elsewhere);
        Assert.False(kind.None);
    }

    [Fact]
    public void Compile_includes_that_all_point_outside_put_the_sources_elsewhere()
    {
        var kind = ProjectSources.Read("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="..\Shared\A.cs" />
                <Compile Include="../Shared/B.cs" />
              </ItemGroup>
            </Project>
            """);

        Assert.True(kind.Elsewhere);
        Assert.False(kind.None);
    }

    /// <summary>
    /// One include of its own is enough: the project owns a document whose location can be
    /// scoped to it, which is the whole requirement.
    /// </summary>
    [Fact]
    public void One_compile_include_of_its_own_outweighs_the_linked_ones()
    {
        var kind = ProjectSources.Read("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="..\Shared\A.cs" />
                <Compile Include="Own.cs" />
              </ItemGroup>
            </Project>
            """);

        Assert.False(kind.Elsewhere);
    }

    /// <summary>
    /// <c>Remove</c> and <c>Update</c> name items the default glob already produced, so
    /// neither says anything about where the sources live — and reading them as includes
    /// would call an ordinary project's exclusion list "sources elsewhere".
    /// </summary>
    [Fact]
    public void Compile_remove_and_update_are_not_includes()
    {
        var kind = ProjectSources.Read("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Remove="..\Legacy\**" />
                <Compile Update="Resources.Designer.cs" />
              </ItemGroup>
            </Project>
            """);

        Assert.False(kind.Elsewhere);
        Assert.False(kind.None);
    }

    /// <summary>
    /// An item group inside a <c>Target</c> or a <c>Choose</c> is still an item group, and a
    /// project written against the old MSBuild namespace carries one an SDK-style project does
    /// not — so the reader matches on local names at any depth.
    /// </summary>
    [Fact]
    public void The_old_msbuild_namespace_is_read_the_same_way()
    {
        var kind = ProjectSources.Read("""
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <EnableDefaultItems>true</EnableDefaultItems>
              </PropertyGroup>
              <Choose>
                <When Condition="'$(Shared)'=='true'">
                  <ItemGroup>
                    <Compile Include="..\Shared\A.cs" />
                  </ItemGroup>
                </When>
              </Choose>
            </Project>
            """);

        Assert.True(kind.Elsewhere);
    }

    /// <summary>
    /// A <c>.csproj</c> somewhere in a large tree that no longer parses must not take
    /// readiness inference down with it. Guessing "ordinary project" costs only the candidates
    /// a scan would have found anyway.
    /// </summary>
    [Fact]
    public void A_malformed_csproj_reads_as_an_ordinary_project()
    {
        var kind = ProjectSources.Read("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>");

        Assert.False(kind.Elsewhere);
        Assert.False(kind.None);
    }
}
