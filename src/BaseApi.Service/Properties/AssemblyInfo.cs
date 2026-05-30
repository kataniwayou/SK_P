using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("BaseApi.Tests")]

// Plan 19-03 (test-only): NSubstitute uses Castle DynamicProxy, which emits proxy types into the
// dynamic assembly "DynamicProxyGenAssembly2". Stubbing the INTERNAL orchestration seam interfaces
// (IWorkflowGraphLoader / IRedisProjectionWriter / IRedisL2Cleanup) in OrchestrationServicePublishTests
// requires that proxy assembly to see them. This is the canonical mechanism for mocking internal types.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
