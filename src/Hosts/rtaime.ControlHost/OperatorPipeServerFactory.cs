// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace rtaime.ControlHost;

internal static class OperatorPipeServerFactory
{
	private const string OperatorPipeSidEnvironment = "RTAIME_OPERATOR_PIPE_SID";

	public static NamedPipeServerStream Create(string endpoint, PipeDirection direction)
	{
		var operatorPipeSid = Environment.GetEnvironmentVariable(OperatorPipeSidEnvironment);
		if (string.IsNullOrWhiteSpace(operatorPipeSid))
		{
			return new NamedPipeServerStream(
				endpoint,
				direction,
				NamedPipeServerStream.MaxAllowedServerInstances,
				PipeTransmissionMode.Byte,
				PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		}

		if (!OperatingSystem.IsWindows())
			throw new PlatformNotSupportedException("Explicit Operator Named Pipe ACLs are supported only on Windows.");

		var security = CreateSecurity(operatorPipeSid);
		return NamedPipeServerStreamAcl.Create(
			endpoint,
			direction,
			NamedPipeServerStream.MaxAllowedServerInstances,
			PipeTransmissionMode.Byte,
			PipeOptions.Asynchronous,
			0,
			0,
			security);
	}

	private static PipeSecurity CreateSecurity(string operatorPipeSid)
	{
		var serviceSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
		var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
		SecurityIdentifier operatorSid;
		try
		{
			operatorSid = new SecurityIdentifier(operatorPipeSid);
		}
		catch (ArgumentException exception)
		{
			throw new InvalidOperationException("RTAIME_OPERATOR_PIPE_SID is not a valid Windows SID.", exception);
		}

		var security = new PipeSecurity();
		security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
		security.SetOwner(serviceSid);
		security.AddAccessRule(new PipeAccessRule(serviceSid, PipeAccessRights.FullControl, AccessControlType.Allow));
		security.AddAccessRule(new PipeAccessRule(administratorsSid, PipeAccessRights.FullControl, AccessControlType.Allow));
		security.AddAccessRule(new PipeAccessRule(operatorSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
		return security;
	}
}
