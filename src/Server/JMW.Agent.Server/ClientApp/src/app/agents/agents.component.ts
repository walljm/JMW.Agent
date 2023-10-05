import { Component, Inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { lastValueFrom } from 'rxjs';

@Component({
  selector: 'app-agents',
  templateUrl: './agents.component.html',
})
export class AgentsComponent {
  public agents: Agent[] = [];

  constructor(http: HttpClient, @Inject('BASE_URL') baseUrl: string) {
    lastValueFrom(http.get<Agent[]>(baseUrl + 'api/v1/server/agents')).then(
      (result) => {
        this.agents = result;
      },
      (error) => console.error(error)
    );
  }
}
interface Agent {
  serviceName: string;
  machineInformation: MachineInformation;
}

interface MachineInformation {
  machineName: string;
  operatingSystem: OperatingSystem;
  processor: Processor;
  systemInfo: SystemInfo;
  userInfo: UserInfo;
  dotNetInfo: DotNetInfo;
  drives: Drive[];
  printers: string[];
  ipGlobalProperties: IpGlobalProperties;
  interfaces: NetworkInterface[];
}
interface DotNetInfo {
  version: Version;
  frameworkDescription: string;
  runtimeIdentifier: string;
}

interface Drive {
  name: string;
  isReady: boolean;
  rootDirectory: string;
  driveType: string;
  driveFormat: string;
  availableFreeSpace: string;
  totalFreeSpace: string;
  totalSize: string;
  volumeLabel: string;
}

interface IpGlobalProperties {
  dhcpScopeName: string;
  domainName: string;
  hostName: string;
  isWinsProxy: boolean;
  nodeType: string;
}

interface UserInfo {
  userName: string;
  userDomainName: string;
}

interface SystemInfo {
  systemDateTime: string;
  systemPageSize: string;
  totalAvailableMemoryBytes: string;
}

interface Processor {
  processorCount: number;
  processArchitecture: string;
  is64BitOperatingSystem: boolean;
  processorIdentity: string;
  processorLevel: string;
  processorRevision: string;
}

interface OperatingSystem {
  Platform: string;
  ServicePack: string;
  Version: Version;
  VersionString: string;
  Architecture: string;
  Description: string;
}

interface Version {
  Major: number;
  Minor: number;
  Build: number;
  Revision: number;
  MajorRevision: number;
  MinorRevision: number;
}

interface NetworkInterface {
  iPv6LoopbackInterfaceIndex: number;
  loopbackInterfaceIndex: number;
  id: string;
  name: string;
  description: string;
  physicalAddress: string;
  operationalStatus: string;
  speed: string;
  isReceiveOnly: boolean;
  supportsMulticast: boolean;
  networkInterfaceType: string;
  networkInterfaceTypeDescription: string;
  iPInterfaceStatistics: IpInterfaceStatistics;
  iPv4InterfaceStatistics: IpInterfaceStatistics;
  iPInterfaceProperties: IpInterfaceProperties;
}

interface IpInterfaceStatistics {
  bytesReceived: string;
  bytesSent: string;
  incomingPacketsDiscarded: string;
  incomingPacketsWithErrors: string;
  incomingUnknownProtocolPackets: string;
  nonUnicastPacketsReceived: string;
  nonUnicastPacketsSent: string;
  outgoingPacketsDiscarded: string;
  outgoingPacketsWithErrors: string;
  outputQueueLength: string;
  unicastPacketsReceived: string;
  unicastPacketsSent: string;
}

interface IpInterfaceProperties {
  isDnsEnabled: boolean;
  dnsSuffix: string;
  isDynamicDnsEnabled: boolean;
  unicastAddresses: UnicastIpAddressInformation[];
  multicastAddresses: MulticastIpAddressInformation[];
  anycastAddresses: IpAddressInformation[];
  dnsAddresses: IpAddress[];
  gatewayAddresses: IpAddress[];
  dhcpServerAddresses: IpAddress[];
  winsServersAddresses: IpAddress[];
  iPv4Properties: Ipv4InterfaceProperties;
  iPv6Properties: Ipv6InterfaceProperties;
}

interface Ipv6InterfaceProperties {
  index: number;
  mtu: number;
}

interface Ipv4InterfaceProperties {
  usesWins: boolean;
  isDhcpEnabled: boolean;
  isAutomaticPrivateAddressingActive: boolean;
  isAutomaticPrivateAddressingEnabled: boolean;
  index: number;
  isForwardingEnabled: boolean;
  mtu: number;
}

interface MulticastIpAddressInformation extends IpAddressInformation {
  addressPreferredLifetime: string;
  addressValidLifetime: string;
  dhcpLeaseLifetime: string;
  duplicateAddressDetectionState: string;
  prefixOrigin: string;
  suffixOrigin: string;
}

interface UnicastIpAddressInformation extends IpAddressInformation {
  addressPreferredLifetime: string;
  addressValidLifetime: string;
  dhcpLeaseLifetime: string;
  duplicateAddressDetectionState: string;
  prefixOrigin: string;
  suffixOrigin: string;
  iPv4Mask: IpAddress;
}

interface IpAddressInformation {
  address: IpAddress;
  isDnsEligible: boolean;
  isTransient: boolean;
}

interface IpAddress {
  address: string;
  addressFamily: string;
  isIPv6Multicast: boolean;
  isIPv6LinkLocal: boolean;
  isIPv6SiteLocal: boolean;
  isIPv6Teredo: boolean;
  isIPv6UniqueLocal: boolean;
  isIPv4MappedToIPv6: boolean;
}
