using System;
using System.Collections.Generic;
using System.Text;

namespace EnderEye.XBOX
{
	using System;

	public class DeviceInfo
	{
		/// <summary>
		/// 应用程序类型
		/// </summary>
		public string ApplicationType { get; set; } = "MinecraftPE";

		/// <summary>
		/// 游戏版本
		/// </summary>
		public string GameVersion { get; set; } = "1.20.62";

		/// <summary>
		/// 设备ID
		/// </summary>
		public string Id { get; set; } = "c1681ad3-415e-30cd-abd3-3b8f51e771d1";

		/// <summary>
		/// 内存大小（字节）
		/// </summary>
		public string Memory { get; set; } = "8589934592";

		/// <summary>
		/// 平台
		/// </summary>
		public string Platform { get; set; } = "Windows10";

		/// <summary>
		/// PlayFab标题ID
		/// </summary>
		public string PlayFabTitleId { get; set; } = "20CA2";

		/// <summary>
		/// 商店平台
		/// </summary>
		public string StorePlatform { get; set; } = "uwp.store";

		/// <summary>
		/// 设备类型
		/// </summary>
		public string Type { get; set; } = "Windows10";
	}

	public class UserInfo
	{
		/// <summary>
		/// 用户令牌
		/// </summary>
		public string Token { get; set; } = "89E497D6112B6ECD-B63A0803D3653643-1B4BAC17CC3B53B6-20CA2-8DE728A0BD7B29B-4mHZeK96t26/b26JRxh3GJJoypeFcpTDnMbSTB4McFU=";

		/// <summary>
		/// 令牌类型
		/// </summary>
		public string TokenType { get; set; } = "PlayFab";
	}

	public class RootObject
	{
		/// <summary>
		/// 设备信息
		/// </summary>
		public DeviceInfo Device { get; set; } = new DeviceInfo();

		/// <summary>
		/// 用户信息
		/// </summary>
		public UserInfo User { get; set; } = new UserInfo();
	}
}
