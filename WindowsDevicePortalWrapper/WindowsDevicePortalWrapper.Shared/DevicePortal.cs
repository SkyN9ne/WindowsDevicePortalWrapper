﻿//----------------------------------------------------------------------------------------------
// <copyright file="DevicePortal.cs" company="Microsoft Corporation">
//     Licensed under the MIT License. See LICENSE.TXT in the project root license information.
// </copyright>
//----------------------------------------------------------------------------------------------

using System;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace Microsoft.Tools.WindowsDevicePortal
{
    /// <summary>
    /// DevicePortal object.
    /// </summary>
    public partial class DevicePortal
    {
        /// <summary>
        /// Issuer for the device certificate.
        /// </summary>
        public static readonly string DevicePortalCertificateIssuer = "Microsoft Windows Web Management";

        /// <summary>
        /// Endpoint used to access the certificate.
        /// </summary>
        private static readonly string RootCertificateEndpoint = "config/rootcertificate";

        /// <summary>
        /// Header name for a CSRF-Token
        /// </summary>
        private static readonly string CsrfTokenName = "CSRF-Token";
        
        /// <summary>
        /// CSRF token retrieved by GET calls and used on subsequent POST/DELETE/PUT calls.
        /// This token is intended to prevent a security vulnerability from cross site forgery.
        /// </summary>
        private string csrfToken = string.Empty;

        /// <summary>
        /// Device connection object.
        /// </summary>
        private IDevicePortalConnection deviceConnection;

        /// <summary>
        /// Initializes a new instance of the <see cref="DevicePortal" /> class.
        /// </summary>
        /// <param name="connection">Implementation of a connection object.</param>
        public DevicePortal(IDevicePortalConnection connection)
        {
            this.deviceConnection = connection;
        }

        /// <summary>
        /// Gets the device address.
        /// </summary>
        public string Address 
        {
            get { return this.deviceConnection.Connection.Authority; }
        }
        
        /// <summary>
        /// Gets or sets handler for reporting connection status.
        /// </summary>
        public DeviceConnectionStatusEventHandler ConnectionStatus
        {
            get;
            set;
        }

        /// <summary>
        /// Gets the status code for establishing our connection.
        /// </summary>
        public HttpStatusCode ConnectionHttpStatusCode
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the device operating system family.
        /// </summary>
        public string DeviceFamily
        {
            get
            {
                return (this.deviceConnection.Family != null) ? this.deviceConnection.Family : string.Empty;
            }
        }

        /// <summary>
        /// Gets the operating system version.
        /// </summary>
        public string OperatingSystemVersion
        {
            get
            {
                return (this.deviceConnection.OsInfo != null) ? this.deviceConnection.OsInfo.OsVersionString : string.Empty;
            }
        }

        /// <summary>
        /// Gets the device platform.
        /// </summary>
        public DevicePortalPlatforms Platform
        {
            get
            {
                return (this.deviceConnection.OsInfo != null) ? this.deviceConnection.OsInfo.Platform : DevicePortalPlatforms.Unknown;
            }
        }

        /// <summary>
        /// Gets the device platform name.
        /// </summary>
        public string PlatformName
        {
            get
            {
                return (this.deviceConnection.OsInfo != null) ? this.deviceConnection.OsInfo.PlatformName : "Unknown";
            }
        }

        /// <summary>
        /// Connects to the device pointed to by IDevicePortalConnection provided in the constructor.
        /// </summary>
        /// <param name="ssid">Optional network SSID.</param>
        /// <param name="ssidKey">Optional network key.</param>
        /// <param name="updateConnection">Indicates whether we should update this connection's IP address after connecting.</param>
        /// <remarks>Connect sends ConnectionStatus events to indicate the current progress in the connection process.
        /// Some applications may opt to not register for the ConnectionStatus event and await on Connect.</remarks>
        /// <returns>Task for tracking the connect.</returns>
        public async Task Connect(
            string ssid = null,
            string ssidKey = null,
            bool updateConnection = true)
        {
            this.ConnectionHttpStatusCode = HttpStatusCode.OK;
            string connectionPhaseDescription = string.Empty;

            // TODO - add status event. this can take a LONG time
            try 
            {
                // Get the device certificate
                bool certificateAcquired = false;
                try
                {
                    connectionPhaseDescription = "Acquiring device certificate";
                    this.SendConnectionStatus(
                        DeviceConnectionStatus.Connecting,
                        DeviceConnectionPhase.AcquiringCertificate,
                        connectionPhaseDescription);
                    this.deviceConnection.SetDeviceCertificate(await this.GetDeviceCertificate());
                    certificateAcquired = true;
                }
                catch
                {
                    // This device does not support the root certificate endpoint.
                    this.SendConnectionStatus(
                        DeviceConnectionStatus.Connecting,
                        DeviceConnectionPhase.AcquiringCertificate,
                        "No device certificate available");
                }

                // Get the device family and operating system information.
                connectionPhaseDescription = "Requesting operating system information";
                this.SendConnectionStatus(
                    DeviceConnectionStatus.Connecting,
                    DeviceConnectionPhase.RequestingOperatingSystemInformation,
                    connectionPhaseDescription);
                this.deviceConnection.Family = await this.GetDeviceFamily();
                this.deviceConnection.OsInfo = await this.GetOperatingSystemInformation();

                // Default to using HTTPS if we were successful in acquiring the device's root certificate.
                bool requiresHttps = certificateAcquired;

                // HoloLens is the only device that supports the GetIsHttpsRequired method.
                if (this.deviceConnection.OsInfo.Platform == DevicePortalPlatforms.HoloLens)
                {
                    // Check to see if HTTPS is required to communicate with this device.
                    connectionPhaseDescription = "Checking secure connection requirements";
                    this.SendConnectionStatus(
                        DeviceConnectionStatus.Connecting,
                        DeviceConnectionPhase.DeterminingConnectionRequirements,
                        connectionPhaseDescription);
                    requiresHttps = await this.GetIsHttpsRequired();
                }

                // Connect the device to the specified network.
                if (!string.IsNullOrWhiteSpace(ssid))
                {
                    connectionPhaseDescription = string.Format("Connecting to {0} network", ssid);
                    this.SendConnectionStatus(
                        DeviceConnectionStatus.Connecting,
                        DeviceConnectionPhase.ConnectingToTargetNetwork,
                        connectionPhaseDescription);
                    WifiInterfaces wifiInterfaces = await this.GetWifiInterfaces();

                    // TODO - consider what to do if there is more than one wifi interface on a device
                    await this.ConnectToWifiNetwork(wifiInterfaces.Interfaces[0].Guid, ssid, ssidKey);

                    // TODO - note that in some instances, the hololens was receiving a KeepAlive exception, yet the network connection succeeded. 
                    // this COULD have been an RTM bug that is now fixed, or it could have been the fault of the access point
                    // some investigation and defensive measures should be implemented here to avoid excessive noise / false failures
                }

                // Get the device's IP configuration and update the connection as appropriate.
                if (updateConnection)
                {
                    connectionPhaseDescription = "Updating device connection";
                    this.SendConnectionStatus(
                        DeviceConnectionStatus.Connecting,
                        DeviceConnectionPhase.UpdatingDeviceAddress,
                        connectionPhaseDescription);
                    this.deviceConnection.UpdateConnection(await this.GetIpConfig(), requiresHttps);
                }

                this.SendConnectionStatus(
                    DeviceConnectionStatus.Connected,
                    DeviceConnectionPhase.Idle,
                    "Device connection established");
            }
            catch (Exception e)
            {
                DevicePortalException dpe = e as DevicePortalException;

                if (dpe != null)
                {
                    this.ConnectionHttpStatusCode = dpe.StatusCode;
                }
                else
                {
                    this.ConnectionHttpStatusCode = (HttpStatusCode)0;
                }

                this.SendConnectionStatus(
                    DeviceConnectionStatus.Failed,
                    DeviceConnectionPhase.Idle,
                    string.Format("Device connection failed: {0}", connectionPhaseDescription));
            }
        }

        /// <summary>
        /// Gets the device certificate as a byte array
        /// </summary>
        /// <returns>Raw device certificate</returns>
        private async Task<byte[]> GetDeviceCertificate()
        {
            byte[] certificateData = null;
            bool useHttps = true;

            // try https then http
            while (true)
            {
                Uri uri = null;

                if (useHttps)
                {
                    uri = Utilities.BuildEndpoint(this.deviceConnection.Connection, RootCertificateEndpoint);
                }
                else
                {
                    Uri baseUri = new Uri(string.Format("http://{0}", this.deviceConnection.Connection.Authority));
                    uri = Utilities.BuildEndpoint(baseUri, RootCertificateEndpoint);
                }

                try
                {
                    using (Stream stream = await this.Get(uri, false))
                    {
                        using (BinaryReader reader = new BinaryReader(stream))
                        {
                            byte[] certData = reader.ReadBytes((int)stream.Length);

                            // Validate the issuer.
                            X509Certificate2 cert = new X509Certificate2(certData);
                            if (!cert.IssuerName.Name.Contains(DevicePortalCertificateIssuer))
                            {
                                throw new DevicePortalException(
                                    (HttpStatusCode)0,
                                    "Invalid certificate issuer",
                                    uri,
                                    "Failed to get device certificate");
                            }

                            certificateData = certData;
                        }
                    }

                    return certificateData;
                }
                catch (Exception e)
                {
                    if (useHttps)
                    {
                        useHttps = false;
                    }
                    else
                    {
                        throw e;
                    }
                }
            }
        }

        /// <summary>
        /// Sends the connection status back to the caller
        /// </summary>
        /// <param name="status">Status of the connect attempt.</param>
        /// <param name="phase">Current phase of the connection attempt.</param>
        /// <param name="message">Optional message describing the connection status.</param>
        private void SendConnectionStatus(
            DeviceConnectionStatus status,
            DeviceConnectionPhase phase,
            string message = "")
        {
            this.ConnectionStatus?.Invoke(this, new DeviceConnectionStatusEventArgs(status, phase, message));
        }
    }
}