using System;
using System.Collections.Generic;
using System.Text;

namespace EnderEye.https
{
	public class DisVery
	{
		public Result result { get; set; }
	}

	public class Result
	{
		public ServiceEnvironments serviceEnvironments { get; set; }
		public Dictionary<string, List<string>> supportedEnvironments { get; set; }
	}

	public class ServiceEnvironments
	{
		public AuthEnvironment auth { get; set; }
	}

	public class AuthEnvironment
	{
		public EnvironmentDetail prod { get; set; }
	}

	public class EnvironmentDetail
	{
		public string serviceUri { get; set; }
		public string issuer { get; set; }
		public string playfabTitleId { get; set; }
		public string eduPlayFabTitleId { get; set; }
	}
}
