﻿using linker.libs;
using linker.libs.extends;
using Microsoft.Win32.SafeHandles;
using System.Net;
using System.Runtime.InteropServices;

namespace linker.tun
{
    internal sealed class LinkerLinuxTunDevice : ILinkerTunDevice
    {

        private string name = string.Empty;
        public string Name => name;
        public bool Running => fs != null;

        private string interfaceLinux = string.Empty;
        private FileStream fs = null;
        private SafeFileHandle safeFileHandle;
        private IPAddress address;
        private byte prefixLength = 24;

        public LinkerLinuxTunDevice(string name)
        {
            this.name = name;
        }

        public bool Setup(IPAddress address, IPAddress gateway, byte prefixLength, out string error)
        {
            error = string.Empty;
            this.address = address;
            this.prefixLength = prefixLength;

            if (Running)
            {
                error = ($"Adapter already exists");
                return false;
            }
            if (Create(out error) == false)
            {
                return false;
            }
            if (Open(out error) == false)
            {
                Shutdown();
                return false;
            }

            fs = new FileStream(safeFileHandle, FileAccess.ReadWrite, 1500);
            interfaceLinux = GetLinuxInterfaceNum();
            return true;
        }
        private bool Create(out string error)
        {
            error = string.Empty;

            byte[] ipv6 = IPAddress.Parse("fe80::1818:1818:1818:1818").GetAddressBytes();
            address.GetAddressBytes().CopyTo(ipv6, ipv6.Length - 4);

            CommandHelper.Linux(string.Empty, new string[] {
                $"ip tuntap add mode tun dev {Name}",
                $"ip addr add {address}/{prefixLength} dev {Name}",
                $"ip addr add {new IPAddress(ipv6)}/64 dev {Name}",
                $"ip link set dev {Name} up"
            });

            string str = CommandHelper.Linux(string.Empty, new string[] { $"ifconfig" });
            if (str.Contains(Name) == false)
            {
                error = CommandHelper.Linux(string.Empty, new string[] { $"ip tuntap add mode tun dev {Name}" });
                return false;
            }

            return true;
        }
        private bool Open(out string error)
        {
            error = string.Empty;

            SafeFileHandle _safeFileHandle = File.OpenHandle("/dev/net/tun", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.Asynchronous);
            if (_safeFileHandle.IsInvalid)
            {
                _safeFileHandle?.Dispose();
                Shutdown();
                error = $"open file /dev/net/tun fail {Marshal.GetLastWin32Error()}";
                return false;
            }

            int ioctl = LinuxAPI.Ioctl(Name, _safeFileHandle, 1074025674);
            if (ioctl != 0)
            {
                _safeFileHandle?.Dispose();
                Shutdown();
                error = $"Ioctl fail : {ioctl}，{Marshal.GetLastWin32Error()}";
                return false;
            }
            safeFileHandle = _safeFileHandle;

            return true;
        }

        public void Shutdown()
        {
            try
            {
                safeFileHandle?.Dispose();
                safeFileHandle = null;

                try
                {
                    fs?.Flush();
                }
                catch (Exception)
                {
                }
                fs?.Close();
                fs?.Dispose();
            }
            catch (Exception)
            {
            }

            fs = null;
            interfaceLinux = string.Empty;
            CommandHelper.Linux(string.Empty, new string[] { $"ip link del {Name}", $"ip tuntap del mode tun dev {Name}" });
        }

        public void SetMtu(int value)
        {
            CommandHelper.Linux(string.Empty, new string[] { $"ip link set dev {Name} mtu {value}" });
        }

        private string GetDefaultInterface()
        {
            return CommandHelper.Linux(string.Empty, ["ip route show default | awk '{print $5}'"]);
        }
        public void SetNat(out string error)
        {
            error = string.Empty;
            try
            {
                IPAddress network = NetworkHelper.ToNetworkIp(address, NetworkHelper.GetPrefixIP(prefixLength));
                CommandHelper.Linux(string.Empty, new string[] {
                    $"sysctl -w net.ipv4.ip_forward=1",

                    $"iptables-legacy -t nat -A POSTROUTING -o {Name} -j MASQUERADE",
                    $"iptables-legacy -A FORWARD -i {interfaceLinux} -o {Name} -j ACCEPT",
                    $"iptables-legacy -A FORWARD -i {Name} -o {interfaceLinux} -m state --state ESTABLISHED,RELATED -j ACCEPT",

                    $"iptables-legacy -A FORWARD -i {Name} -j ACCEPT",
                    $"iptables-legacy -A FORWARD -o {Name} -m state --state ESTABLISHED,RELATED -j ACCEPT",
                    $"iptables-legacy -t nat -A POSTROUTING ! -o {Name} -s {network}/{prefixLength} -j MASQUERADE",
                });
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }
        public void RemoveNat(out string error)
        {
            error = string.Empty;
            try
            {
                CommandHelper.Linux(string.Empty, new string[] {
                    $"iptables-legacy -t nat -D POSTROUTING -o {Name} -j MASQUERADE",
                    $"iptables-legacy -D FORWARD -i {interfaceLinux} -o {Name} -j ACCEPT",
                    $"iptables-legacy -D FORWARD -i {Name} -o {interfaceLinux} -m state --state ESTABLISHED,RELATED -j ACCEPT",

                    $"iptables-legacy -D FORWARD -i {Name} -j ACCEPT",
                    $"iptables-legacy -D FORWARD -o {Name} -m state --state ESTABLISHED,RELATED -j ACCEPT"
                });

                IPAddress network = NetworkHelper.ToNetworkIp(address, NetworkHelper.GetPrefixIP(prefixLength));
                string iptableLineNumbers = CommandHelper.Linux(string.Empty, new string[] { $"iptables-legacy -t nat -L --line-numbers | grep {network}/{prefixLength} | cut -d' ' -f1" });
                if (string.IsNullOrWhiteSpace(iptableLineNumbers) == false)
                {
                    string[] commands = iptableLineNumbers.Split(Environment.NewLine)
                        .Where(c => string.IsNullOrWhiteSpace(c) == false)
                        .Select(c => $"iptables-legacy -t nat -D POSTROUTING {c}").ToArray();
                    CommandHelper.Linux(string.Empty, commands);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }


        public void AddForward(List<LinkerTunDeviceForwardItem> forwards)
        {
            string[] commands = forwards.Where(c => c != null && c.Enable).SelectMany(c =>
            {
                return new string[] {
                    $"sysctl -w net.ipv4.ip_forward=1",
                    $"iptables-legacy -t nat -A PREROUTING -p tcp --dport {c.ListenPort} -j DNAT --to-destination {c.ConnectAddr}:{c.ConnectPort}",
                    $"iptables-legacy -t nat -A POSTROUTING -p tcp --dport {c.ConnectPort} -j MASQUERADE",
                    $"iptables-legacy -t nat -A PREROUTING -p udp --dport {c.ListenPort} -j DNAT --to-destination {c.ConnectAddr}:{c.ConnectPort}",
                    $"iptables-legacy -t nat -A POSTROUTING -p udp --dport {c.ConnectPort} -j MASQUERADE",
                };

            }).ToArray();
            CommandHelper.Linux(string.Empty, commands);
        }
        public void RemoveForward(List<LinkerTunDeviceForwardItem> forwards)
        {
            string[] commands = forwards.Where(c => c != null && c.Enable).SelectMany(c =>
            {
                return new string[] {
                    $"sysctl -w net.ipv4.ip_forward=1",
                    $"iptables-legacy -t nat -D PREROUTING -p tcp --dport {c.ListenPort} -j DNAT --to-destination {c.ConnectAddr}:{c.ConnectPort}",
                    $"iptables-legacy -t nat -D POSTROUTING -p tcp --dport {c.ConnectPort} -j MASQUERADE",
                    $"iptables-legacy -t nat -D PREROUTING -p udp --dport {c.ListenPort} -j DNAT --to-destination {c.ConnectAddr}:{c.ConnectPort}",
                    $"iptables-legacy -t nat -D POSTROUTING -p udp --dport {c.ConnectPort} -j MASQUERADE"
                };

            }).ToArray();
            CommandHelper.Linux(string.Empty, commands);
        }


        public void AddRoute(LinkerTunDeviceRouteItem[] ips, IPAddress ip)
        {
            string[] commands = ips.Select(item =>
            {
                uint prefixValue = NetworkHelper.GetPrefixIP(item.PrefixLength);
                IPAddress network = NetworkHelper.ToNetworkIp(item.Address, prefixValue);

                return $"ip route add {network}/{item.PrefixLength} via {ip} dev {Name} metric 1 ";
            }).ToArray();
            if (commands.Length > 0)
            {
                CommandHelper.Linux(string.Empty, commands);
            }
        }
        public void DelRoute(LinkerTunDeviceRouteItem[] ip)
        {
            string[] commands = ip.Select(item =>
            {
                uint prefixValue = NetworkHelper.GetPrefixIP(item.PrefixLength);
                IPAddress network = NetworkHelper.ToNetworkIp(item.Address, prefixValue);
                return $"ip route del {network}/{item.PrefixLength}";
            }).ToArray();
            CommandHelper.Linux(string.Empty, commands);
        }


        private byte[] buffer = new byte[2 * 1024];
        private object writeLockObj = new object();
        public ReadOnlyMemory<byte> Read()
        {
            try
            {
                int length = fs.Read(buffer.AsSpan(4));
                length.ToBytes(buffer);
                return buffer.AsMemory(0, length + 4);
            }
            catch (Exception)
            {
            }
            return Helper.EmptyArray;

        }
        public bool Write(ReadOnlyMemory<byte> buffer)
        {
            lock (writeLockObj)
            {
                try
                {
                    fs.Write(buffer.Span);
                    fs.Flush();
                    return true;
                }
                catch (Exception)
                {
                }
                return false;
            }
        }

        private string GetLinuxInterfaceNum()
        {
            string output = CommandHelper.Linux(string.Empty, new string[] { "ip route show default" });
            foreach (var item in output.Split(Environment.NewLine))
            {
                if (item.StartsWith("default via"))
                {
                    var strs = item.Split(' ');
                    for (int i = 0; i < strs.Length; i++)
                    {
                        if (strs[i] == "dev")
                        {
                            return strs[i + 1].Trim();
                        }
                    }
                }
            }
            return string.Empty;
        }

        public void Clear()
        {
        }


    }


}
