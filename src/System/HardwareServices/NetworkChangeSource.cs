using System;
using System.Net.NetworkInformation;

namespace LiteMonitor.src.SystemServices
{
    /// <summary>网络变更事件的种类。</summary>
    public enum NetworkChangeKind
    {
        /// <summary>地址变更（IP 变化 / 网卡出现或消失）。</summary>
        Address = 0,

        /// <summary>可用性变更（链路 up/down，例如 Wi-Fi ↔ 有线切换）。</summary>
        Availability = 1
    }

    /// <summary>
    /// 网络变更事件源：把系统的网络变更事件抽象出来，测试可以注入假实现。
    /// <para>★ 生产实现只订阅一次 <see cref="NetworkChange"/> 的两个事件
    /// （地址变更 + 可用性变更），再广播给订阅者。</para>
    /// </summary>
    public interface INetworkChangeSource
    {
        /// <summary>网络发生变化（地址变更或可用性变更）。</summary>
        event EventHandler<NetworkChangeKind>? Changed;
    }

    /// <summary>
    /// 生产实现：转发 <see cref="NetworkChange.NetworkAddressChanged"/> 与
    /// <see cref="NetworkChange.NetworkAvailabilityChanged"/>。
    /// <para>★ 系统事件**只在进程内订阅一次**（静态单例）—— 避免每创建一个
    /// <c>NetworkManager</c> 就往**系统**静态事件上再挂一个处理器。</para>
    /// <para>★ 但注意：每个 <c>NetworkManager</c> 实例仍会向**本单例**挂一个
    /// <c>Changed</c> 处理器，且**没有解绑路径**（生产里 <c>NetworkManager</c>
    /// 只创建一次，见 <c>HardwareMonitor</c>；实例不会被反复创建）。
    /// ⇒ **测试必须注入假事件源**，否则会跨用例串台、并让旧实例被单例强引用住。</para>
    /// </summary>
    public sealed class SystemNetworkChangeSource : INetworkChangeSource
    {
        /// <summary>进程级共享实例。</summary>
        public static SystemNetworkChangeSource Instance { get; } = new SystemNetworkChangeSource();

        /// <inheritdoc />
        public event EventHandler<NetworkChangeKind>? Changed;

        private SystemNetworkChangeSource()
        {
            NetworkChange.NetworkAddressChanged += OnAddressChanged;
            NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        }

        private void OnAddressChanged(object? sender, EventArgs e)
        {
            Raise(NetworkChangeKind.Address);
        }

        private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
        {
            Raise(NetworkChangeKind.Availability);
        }

        private void Raise(NetworkChangeKind kind)
        {
            try
            {
                Changed?.Invoke(this, kind);
            }
            catch
            {
                // 事件订阅者出错不得影响其它订阅者（也不得让系统事件回调抛出）。
            }
        }
    }
}
