﻿namespace Unosquare.FFME.Rendering.Wave
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Security;
    using System.Threading;

    /// <summary>
    /// NativeDirectSoundOut using DirectSound COM interop.
    /// Contact author: Alexandre Mutel - alexandre_mutel at yahoo.fr
    /// Modified by: Graham "Gee" Plumb
    /// </summary>
    internal sealed class DirectSoundPlayer : IWavePlayer
    {
        // TODO: log/bubble errors to renderer/MediaElement
        #region Fields

        /// <summary>
        /// DirectSound default playback device GUID
        /// </summary>
        public static readonly Guid DefaultPlaybackDeviceId = new Guid("DEF00000-9C6D-47ED-AAF1-4DDA8F2B5C03");

        // Device enumerations
        private static readonly object DevicesEnumLock = new object();
        private static List<DirectSoundDeviceInfo> EnumeratedDevices;

        private readonly object SyncLock = new object();
        private readonly SynchronizationContext SyncContext;
        private WaveFormat WaveFormat;
        private long BytesPlayedCount;
        private int SamplesTotalSize;
        private int SamplesFrameSize;
        private int NextSamplesWriteIndex;
        private Guid DeviceId;
        private byte[] Samples;
        private IWaveProvider WaveStream = null;
        private DirectSound.IDirectSound DirectSoundDriver = null;
        private DirectSound.IDirectSoundBuffer PrimarySoundBuffer = null;
        private DirectSound.IDirectSoundBuffer SecondaryBuffer = null;
        private EventWaitHandle FrameEventWaitHandle1;
        private EventWaitHandle FrameEventWaitHandle2;
        private EventWaitHandle EndEventWaitHandle;
        private Thread PlaybackThread;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="DirectSoundPlayer" /> class.
        /// </summary>
        /// <param name="renderer">The renderer.</param>
        public DirectSoundPlayer(AudioRenderer renderer)
            : this(renderer, DefaultPlaybackDeviceId)
        {
            // placeholder
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DirectSoundPlayer" /> class.
        /// </summary>
        /// <param name="renderer">The renderer.</param>
        /// <param name="deviceId">The device.</param>
        public DirectSoundPlayer(AudioRenderer renderer, Guid deviceId)
            : this(renderer, deviceId, 40)
        {
            // placeholder
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DirectSoundPlayer" /> class.
        /// </summary>
        /// <param name="renderer">The renderer.</param>
        /// <param name="desiredLatency">The latency.</param>
        public DirectSoundPlayer(AudioRenderer renderer, int desiredLatency)
            : this(renderer, DefaultPlaybackDeviceId, desiredLatency)
        {
            // placeholder
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DirectSoundPlayer" /> class.
        /// (40ms seems to work under Vista).
        /// </summary>
        /// <param name="renderer">The renderer.</param>
        /// <param name="deviceId">Selected device</param>
        /// <param name="desiredLatency">The latency.</param>
        public DirectSoundPlayer(AudioRenderer renderer, Guid deviceId, int desiredLatency)
        {
            Renderer = renderer;

            if (deviceId == Guid.Empty)
            {
                deviceId = DefaultPlaybackDeviceId;
            }

            DeviceId = deviceId;
            DesiredLatency = desiredLatency;
            SyncContext = SynchronizationContext.Current;
        }

        /// <summary>
        /// Finalizes an instance of the <see cref="DirectSoundPlayer"/> class.
        /// Releases unmanaged resources and performs other cleanup operations before the
        /// <see cref="DirectSoundPlayer"/> is reclaimed by garbage collection.
        /// </summary>
        ~DirectSoundPlayer()
        {
            Dispose();
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the renderer that owns this wave player.
        /// </summary>
        public AudioRenderer Renderer { get; }

        /// <summary>
        /// Gets the current position from the wave output device.
        /// </summary>
        public TimeSpan PlaybackPosition
        {
            get
            {
                // bytes played in this stream
                var pos = GetPosition();

                // samples played in this stream
                pos /= WaveFormat.Channels * WaveFormat.BitsPerSample / 8;

                // ms played in this stream
                return TimeSpan.FromMilliseconds(pos * 1000.0 / WaveFormat.SampleRate);
            }
        }

        /// <summary>
        /// Current playback state
        /// </summary>
        public PlaybackState PlaybackState { get; private set; }

        /// <summary>
        /// Gets or sets the desired latency in milliseconds
        /// Should be set before a call to Init
        /// </summary>
        public int DesiredLatency { get; set; }

        #endregion

        #region Public Methods

        /// <summary>
        /// Gets the DirectSound output devices in the system
        /// </summary>
        /// <returns>The available DirectSound devices</returns>
        public static List<DirectSoundDeviceInfo> EnumerateDevices()
        {
            lock (DevicesEnumLock)
            {
                EnumeratedDevices = new List<DirectSoundDeviceInfo>(32);
                NativeMethods.DirectSoundEnumerateA(new DirectSound.EnumerateDevicesDelegate(EnumerateDevicesCallback), IntPtr.Zero);
                return EnumeratedDevices;
            }
        }

        /// <summary>
        /// Initialise playback
        /// </summary>
        /// <param name="waveProvider">The waveprovider to be played</param>
        public void Init(IWaveProvider waveProvider)
        {
            WaveStream = waveProvider;
            WaveFormat = waveProvider.WaveFormat;
        }

        /// <summary>
        /// Begin playback
        /// </summary>
        public void Play()
        {
            if (PlaybackState == PlaybackState.Stopped)
            {
                // Thread that processes samples
                PlaybackThread = new Thread(new ThreadStart(PerformContinuousPlayback))
                {
                    // put this back to highest when we are confident we don't have any bugs in the thread proc
                    Priority = ThreadPriority.AboveNormal,
                    IsBackground = true
                };

                PlaybackThread.Start();
            }

            lock (SyncLock)
            {
                PlaybackState = PlaybackState.Playing;
            }
        }

        /// <summary>
        /// Stop playback
        /// </summary>
        public void Stop()
        {
            // Try and tidy up nicely
            if (Monitor.TryEnter(SyncLock, 50))
            {
                PlaybackState = PlaybackState.Stopped;
                Monitor.Exit(SyncLock);
            }
            else
            {
                // No joy - abort the thread!
                if (PlaybackThread != null)
                {
                    PlaybackThread.Abort();
                    PlaybackThread = null;
                }
            }
        }

        /// <summary>
        /// Pause Playback
        /// </summary>
        public void Pause()
        {
            lock (SyncLock)
            {
                PlaybackState = PlaybackState.Paused;
            }
        }

        /// <summary>
        /// Gets the current position in bytes from the wave output device.
        /// (n.b. this is not the same thing as the position within your reader
        /// stream)
        /// </summary>
        /// <returns>Position in bytes</returns>
        public long GetPosition()
        {
            if (PlaybackState != PlaybackState.Stopped)
            {
                var sbuf = SecondaryBuffer;
                if (sbuf != null)
                {
                    uint currentPlayCursor, currentWriteCursor;
                    sbuf.GetCurrentPosition(out currentPlayCursor, out currentWriteCursor);
                    return currentPlayCursor + BytesPlayedCount;
                }
            }

            return 0;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Stop();
            EndEventWaitHandle.Dispose();
            FrameEventWaitHandle1.Dispose();
            FrameEventWaitHandle2.Dispose();
            GC.SuppressFinalize(this);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Enumerates the devices.
        /// </summary>
        /// <param name="lpGuid">The lp unique identifier.</param>
        /// <param name="lpcstrDescription">The LPCSTR description.</param>
        /// <param name="lpcstrModule">The LPCSTR module.</param>
        /// <param name="lpContext">The lp context.</param>
        /// <returns>The devices</returns>
        private static bool EnumerateDevicesCallback(IntPtr lpGuid, IntPtr lpcstrDescription, IntPtr lpcstrModule, IntPtr lpContext)
        {
            var device = new DirectSoundDeviceInfo();
            if (lpGuid == IntPtr.Zero)
            {
                device.Guid = Guid.Empty;
            }
            else
            {
                byte[] guidBytes = new byte[16];
                Marshal.Copy(lpGuid, guidBytes, 0, 16);
                device.Guid = new Guid(guidBytes);
            }

            device.Description = Marshal.PtrToStringAnsi(lpcstrDescription);
            if (lpcstrModule != null)
            {
                device.ModuleName = Marshal.PtrToStringAnsi(lpcstrModule);
            }

            EnumeratedDevices.Add(device);
            return true;
        }

        private void InitializeDirectSound()
        {
            // Open DirectSound
            lock (SyncLock)
            {
                DirectSoundDriver = null;
                NativeMethods.DirectSoundCreate(ref DeviceId, out DirectSoundDriver, IntPtr.Zero);

                if (DirectSoundDriver != null)
                {
                    // Set Cooperative Level to PRIORITY (priority level can call the SetFormat and Compact methods)
                    DirectSoundDriver.SetCooperativeLevel(NativeMethods.GetDesktopWindow(), DirectSound.DirectSoundCooperativeLevel.DSSCL_PRIORITY);

                    // -------------------------------------------------------------------------------------
                    // Create PrimaryBuffer
                    // -------------------------------------------------------------------------------------

                    // Fill BufferDescription for PrimaryBuffer
                    var bufferDesc = new DirectSound.BufferDescription();
                    bufferDesc.Size = Marshal.SizeOf(bufferDesc);
                    bufferDesc.BufferBytes = 0;
                    bufferDesc.Flags = DirectSound.DirectSoundBufferCaps.DSBCAPS_PRIMARYBUFFER;
                    bufferDesc.Reserved = 0;
                    bufferDesc.FormatHandle = IntPtr.Zero;
                    bufferDesc.AlgorithmId = Guid.Empty;

                    // Create PrimaryBuffer
                    DirectSoundDriver.CreateSoundBuffer(bufferDesc, out object soundBufferObj, IntPtr.Zero);
                    PrimarySoundBuffer = (DirectSound.IDirectSoundBuffer)soundBufferObj;

                    // Play & Loop on the PrimarySound Buffer
                    PrimarySoundBuffer.Play(0, 0, DirectSound.DirectSoundPlayFlags.DSBPLAY_LOOPING);

                    // -------------------------------------------------------------------------------------
                    // Create SecondaryBuffer
                    // -------------------------------------------------------------------------------------

                    // A frame of samples equals to Desired Latency
                    SamplesFrameSize = MsToBytes(DesiredLatency);

                    // Fill BufferDescription for SecondaryBuffer
                    var bufferDesc2 = new DirectSound.BufferDescription();
                    bufferDesc2.Size = Marshal.SizeOf(bufferDesc2);
                    bufferDesc2.BufferBytes = (uint)(SamplesFrameSize * 2);
                    bufferDesc2.Flags = DirectSound.DirectSoundBufferCaps.DSBCAPS_GETCURRENTPOSITION2
                        | DirectSound.DirectSoundBufferCaps.DSBCAPS_CTRLPOSITIONNOTIFY
                        | DirectSound.DirectSoundBufferCaps.DSBCAPS_GLOBALFOCUS
                        | DirectSound.DirectSoundBufferCaps.DSBCAPS_CTRLVOLUME
                        | DirectSound.DirectSoundBufferCaps.DSBCAPS_STICKYFOCUS
                        | DirectSound.DirectSoundBufferCaps.DSBCAPS_GETCURRENTPOSITION2;
                    bufferDesc2.Reserved = 0;
                    GCHandle handleOnWaveFormat = GCHandle.Alloc(WaveFormat, GCHandleType.Pinned); // Ptr to waveFormat
                    bufferDesc2.FormatHandle = handleOnWaveFormat.AddrOfPinnedObject(); // set Ptr to waveFormat
                    bufferDesc2.AlgorithmId = Guid.Empty;

                    // Create SecondaryBuffer
                    DirectSoundDriver.CreateSoundBuffer(bufferDesc2, out soundBufferObj, IntPtr.Zero);
                    SecondaryBuffer = (DirectSound.IDirectSoundBuffer)soundBufferObj;
                    handleOnWaveFormat.Free();

                    // Get effective SecondaryBuffer size
                    var dsbCaps = new DirectSound.BufferCaps();
                    dsbCaps.Size = Marshal.SizeOf(dsbCaps);
                    SecondaryBuffer.GetCaps(dsbCaps);

                    NextSamplesWriteIndex = 0;
                    SamplesTotalSize = dsbCaps.BufferBytes;
                    Samples = new byte[SamplesTotalSize];
                    Debug.Assert(SamplesTotalSize == (2 * SamplesFrameSize), "Invalid SamplesTotalSize vs SamplesFrameSize");

                    // -------------------------------------------------------------------------------------
                    // Create double buffering notification.
                    // Use DirectSoundNotify at Position [0, 1/2] and Stop Position (0xFFFFFFFF)
                    // -------------------------------------------------------------------------------------
                    var notify = (DirectSound.IDirectSoundNotify)soundBufferObj;

                    FrameEventWaitHandle1 = new EventWaitHandle(false, EventResetMode.AutoReset);
                    FrameEventWaitHandle2 = new EventWaitHandle(false, EventResetMode.AutoReset);
                    EndEventWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);

                    var notifies = new DirectSound.DirectSoundBufferPositionNotify[3];
                    notifies[0] = new DirectSound.DirectSoundBufferPositionNotify
                    {
                        Offset = 0,
                        NotifyHandle = FrameEventWaitHandle1.SafeWaitHandle.DangerousGetHandle()
                    };

                    notifies[1] = new DirectSound.DirectSoundBufferPositionNotify
                    {
                        Offset = (uint)SamplesFrameSize,
                        NotifyHandle = FrameEventWaitHandle2.SafeWaitHandle.DangerousGetHandle()
                    };

                    notifies[2] = new DirectSound.DirectSoundBufferPositionNotify
                    {
                        Offset = 0xFFFFFFFF,
                        NotifyHandle = EndEventWaitHandle.SafeWaitHandle.DangerousGetHandle()
                    };

                    notify.SetNotificationPositions(3, notifies);
                }
            }
        }

        /// <summary>
        /// Determines whether the SecondaryBuffer is lost.
        /// </summary>
        /// <returns>
        /// <c>true</c> if [is buffer lost]; otherwise, <c>false</c>.
        /// </returns>
        private bool IsBufferLost()
        {
            return (SecondaryBuffer.GetStatus() & DirectSound.DirectSoundBufferStatus.DSBSTATUS_BUFFERLOST) != 0 ? true : false;
        }

        /// <summary>
        /// Convert ms to bytes size according to WaveFormat
        /// </summary>
        /// <param name="ms">The ms</param>
        /// <returns>number of byttes</returns>
        private int MsToBytes(int ms)
        {
            int bytes = ms * (WaveFormat.AverageBytesPerSecond / 1000);
            bytes -= bytes % WaveFormat.BlockAlign;
            return bytes;
        }

        /// <summary>
        /// Processes the samples in a separate thread.
        /// </summary>
        private void PerformContinuousPlayback()
        {
            // Used to determine if playback is halted
            var loopPlaybackHalted = false;
            var firstBufferStarted = false;
            BytesPlayedCount = 0;

            Exception exception = null;

            // In case the thread is killed
            try
            {
                InitializeDirectSound();
                var loopResult = 1;

                if (PlaybackState == PlaybackState.Stopped)
                {
                    SecondaryBuffer.SetCurrentPosition(0);
                    NextSamplesWriteIndex = 0;
                    loopResult = Feed(SamplesTotalSize);
                }

                // Incase the previous Feed method returns 0
                if (loopResult > 0)
                {
                    lock (SyncLock)
                    {
                        PlaybackState = PlaybackState.Playing;
                    }

                    SecondaryBuffer.Play(0, 0, DirectSound.DirectSoundPlayFlags.DSBPLAY_LOOPING);

                    var waitHandles = new WaitHandle[] { FrameEventWaitHandle1, FrameEventWaitHandle2, EndEventWaitHandle };

                    var loopContinuePlayback = true;
                    while (PlaybackState != PlaybackState.Stopped && loopContinuePlayback)
                    {
                        // Wait for signals on frameEventWaitHandle1 (Position 0), frameEventWaitHandle2 (Position 1/2)
                        int indexHandle = WaitHandle.WaitAny(waitHandles, 3 * DesiredLatency, false);

                        // TimeOut is ok
                        if (indexHandle != WaitHandle.WaitTimeout)
                        {
                            // Buffer is Stopped
                            if (indexHandle == 2)
                            {
                                // (Gee) - Not sure whether to stop playback in this case or not!
                                StopPlayback();
                                loopPlaybackHalted = true;
                                loopContinuePlayback = false;
                            }
                            else
                            {
                                if (indexHandle == 0)
                                {
                                    // we're at the beginning of the buffer...
                                    if (firstBufferStarted)
                                    {
                                        // because this notification is based on the *playback" cursor, this should be reasonably accurate
                                        BytesPlayedCount += SamplesFrameSize * 2;
                                    }
                                }
                                else
                                {
                                    firstBufferStarted = true;
                                }

                                indexHandle = (indexHandle == 0) ? 1 : 0;
                                NextSamplesWriteIndex = indexHandle * SamplesFrameSize;

                                // Only carry on playing if we can!
                                if (Feed(SamplesFrameSize) == 0)
                                {
                                    StopPlayback();
                                    loopPlaybackHalted = true;
                                    loopContinuePlayback = false;
                                }
                            }
                        }
                        else
                        {
                            // Timed out!
                            StopPlayback();
                            loopPlaybackHalted = true;
                            loopContinuePlayback = false;

                            // report this as an error in the Playback Stopped
                            // seems to happen when device is unplugged
                            throw new Exception("DirectSound buffer timeout");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                // Do nothing (except report error)
                Debug.WriteLine(e.ToString());
                exception = e;
            }
            finally
            {
                if (!loopPlaybackHalted)
                {
                    try
                    {
                        StopPlayback();
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine(e.ToString());

                        // don't overwrite the original reason we exited the playback loop
                        if (exception == null) exception = e;
                    }
                }

                lock (SyncLock)
                {
                    PlaybackState = PlaybackState.Stopped;
                }

                BytesPlayedCount = 0;
            }
        }

        /// <summary>
        /// Stop playback
        /// </summary>
        private void StopPlayback()
        {
            lock (SyncLock)
            {
                if (SecondaryBuffer != null)
                {
                    CleanUpSecondaryBuffer();

                    SecondaryBuffer.Stop();
                    SecondaryBuffer = null;
                }

                if (PrimarySoundBuffer != null)
                {
                    PrimarySoundBuffer.Stop();
                    PrimarySoundBuffer = null;
                }
            }
        }

        /// <summary>
        /// Clean up the SecondaryBuffer
        /// </summary>
        /// <remarks>
        /// <para>
        /// In DirectSound, when playback is started,
        /// the rest of the sound that was played last time is played back as noise.
        /// This happens even if the secondary buffer is completely silenced,
        /// so it seems that the buffer in the primary buffer or higher is not cleared.
        /// </para>
        /// <para>
        /// To solve this problem fill the secondary buffer with silence data when stop playback.
        /// </para>
        /// </remarks>
        private void CleanUpSecondaryBuffer()
        {
            if (SecondaryBuffer != null)
            {
                byte[] silence = new byte[SamplesTotalSize];

                // Lock the SecondaryBuffer
                SecondaryBuffer.Lock(0,
                    (uint)SamplesTotalSize,
                    out IntPtr wavBuffer1,
                    out int nbSamples1,
                    out IntPtr wavBuffer2,
                    out int nbSamples2,
                    DirectSound.DirectSoundBufferLockFlag.None);

                // Copy silence data to the SecondaryBuffer
                if (wavBuffer1 != IntPtr.Zero)
                {
                    Marshal.Copy(silence, 0, wavBuffer1, nbSamples1);
                    if (wavBuffer2 != IntPtr.Zero)
                    {
                        Marshal.Copy(silence, 0, wavBuffer1, nbSamples1);
                    }
                }

                // Unlock the SecondaryBuffer
                SecondaryBuffer.Unlock(wavBuffer1, nbSamples1, wavBuffer2, nbSamples2);
            }
        }

        /// <summary>
        /// Feeds the SecondaryBuffer with the WaveStream
        /// </summary>
        /// <param name="bytesToCopy">number of bytes to feed</param>
        /// <returns>The number of bytes that were read</returns>
        private int Feed(int bytesToCopy)
        {
            int bytesRead = bytesToCopy;

            // Restore the buffer if lost
            if (IsBufferLost())
            {
                SecondaryBuffer.Restore();
            }

            // Clear the bufferSamples if in Paused
            if (PlaybackState == PlaybackState.Paused)
            {
                Array.Clear(Samples, 0, Samples.Length);
            }
            else
            {
                // Read data from stream (Should this be inserted between the lock / unlock?)
                bytesRead = WaveStream.Read(Samples, 0, bytesToCopy);

                if (bytesRead == 0)
                {
                    Array.Clear(Samples, 0, Samples.Length);
                    return 0;
                }
            }

            // Lock a portion of the SecondaryBuffer (starting from 0 or 1/2 the buffer)
            SecondaryBuffer.Lock(NextSamplesWriteIndex,
                (uint)bytesRead,  // (uint)bytesToCopy,
                out IntPtr wavBuffer1,
                out int nbSamples1,
                out IntPtr wavBuffer2,
                out int nbSamples2,
                DirectSound.DirectSoundBufferLockFlag.None);

            // Copy back to the SecondaryBuffer
            if (wavBuffer1 != IntPtr.Zero)
            {
                Marshal.Copy(Samples, 0, wavBuffer1, nbSamples1);
                if (wavBuffer2 != IntPtr.Zero)
                {
                    Marshal.Copy(Samples, 0, wavBuffer1, nbSamples1);
                }
            }

            // Unlock the SecondaryBuffer
            SecondaryBuffer.Unlock(wavBuffer1, nbSamples1, wavBuffer2, nbSamples2);

            return bytesRead;
        }

        #endregion

        #region Native DirectSound COM Interface

        private static class DirectSound
        {
            /// <summary>
            /// DirectSound default capture device GUID
            /// </summary>
            public static readonly Guid DefaultCaptureDeviceId = new Guid("DEF00001-9C6D-47ED-AAF1-4DDA8F2B5C03");

            /// <summary>
            /// DirectSound default device for voice playback
            /// </summary>
            public static readonly Guid DefaultVoicePlaybackDeviceId = new Guid("DEF00002-9C6D-47ED-AAF1-4DDA8F2B5C03");

            /// <summary>
            /// DirectSound default device for voice capture
            /// </summary>
            public static readonly Guid DefaultVoiceCaptureDeviceId = new Guid("DEF00003-9C6D-47ED-AAF1-4DDA8F2B5C03");

            /// <summary>
            /// The DSEnumCallback function is an application-defined callback function that enumerates the DirectSound drivers.
            /// The system calls this function in response to the application's call to the DirectSoundEnumerate or DirectSoundCaptureEnumerate function.
            /// </summary>
            /// <param name="lpGuid">Address of the GUID that identifies the device being enumerated, or NULL for the primary device. This value can be passed to the DirectSoundCreate8 or DirectSoundCaptureCreate8 function to create a device object for that driver. </param>
            /// <param name="lpcstrDescription">Address of a null-terminated string that provides a textual description of the DirectSound device. </param>
            /// <param name="lpcstrModule">Address of a null-terminated string that specifies the module name of the DirectSound driver corresponding to this device. </param>
            /// <param name="lpContext">Address of application-defined data. This is the pointer passed to DirectSoundEnumerate or DirectSoundCaptureEnumerate as the lpContext parameter. </param>
            /// <returns>Returns TRUE to continue enumerating drivers, or FALSE to stop.</returns>
            public delegate bool EnumerateDevicesDelegate(IntPtr lpGuid, IntPtr lpcstrDescription, IntPtr lpcstrModule, IntPtr lpContext);

            public enum DirectSoundCooperativeLevel : uint
            {
                DSSCL_NORMAL = 0x00000001,
                DSSCL_PRIORITY = 0x00000002,
                DSSCL_EXCLUSIVE = 0x00000003,
                DSSCL_WRITEPRIMARY = 0x00000004
            }

            [Flags]
            public enum DirectSoundPlayFlags : uint
            {
                DSBPLAY_LOOPING = 0x00000001,
                DSBPLAY_LOCHARDWARE = 0x00000002,
                DSBPLAY_LOCSOFTWARE = 0x00000004,
                DSBPLAY_TERMINATEBY_TIME = 0x00000008,
                DSBPLAY_TERMINATEBY_DISTANCE = 0x000000010,
                DSBPLAY_TERMINATEBY_PRIORITY = 0x000000020
            }

            [Flags]
            public enum DirectSoundBufferLockFlag : uint
            {
                None = 0,
                FromWriteCursor = 0x00000001,
                EntireBuffer = 0x00000002
            }

            [Flags]
            public enum DirectSoundBufferStatus : uint
            {
                DSBSTATUS_PLAYING = 0x00000001,
                DSBSTATUS_BUFFERLOST = 0x00000002,
                DSBSTATUS_LOOPING = 0x00000004,
                DSBSTATUS_LOCHARDWARE = 0x00000008,
                DSBSTATUS_LOCSOFTWARE = 0x00000010,
                DSBSTATUS_TERMINATED = 0x00000020
            }

            [Flags]
            public enum DirectSoundBufferCaps : uint
            {
                DSBCAPS_PRIMARYBUFFER = 0x00000001,
                DSBCAPS_STATIC = 0x00000002,
                DSBCAPS_LOCHARDWARE = 0x00000004,
                DSBCAPS_LOCSOFTWARE = 0x00000008,
                DSBCAPS_CTRL3D = 0x00000010,
                DSBCAPS_CTRLFREQUENCY = 0x00000020,
                DSBCAPS_CTRLPAN = 0x00000040,
                DSBCAPS_CTRLVOLUME = 0x00000080,
                DSBCAPS_CTRLPOSITIONNOTIFY = 0x00000100,
                DSBCAPS_CTRLFX = 0x00000200,
                DSBCAPS_STICKYFOCUS = 0x00004000,
                DSBCAPS_GLOBALFOCUS = 0x00008000,
                DSBCAPS_GETCURRENTPOSITION2 = 0x00010000,
                DSBCAPS_MUTE3DATMAXDISTANCE = 0x00020000,
                DSBCAPS_LOCDEFER = 0x00040000
            }

            /// <summary>
            /// IDirectSound interface
            /// </summary>
            [ComImport]
            [Guid("279AFA83-4981-11CE-A521-0020AF0BE560")]
            [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            [SuppressUnmanagedCodeSecurity]
            public interface IDirectSound
            {
                void CreateSoundBuffer([In] BufferDescription desc, [Out, MarshalAs(UnmanagedType.Interface)] out object dsDSoundBuffer, IntPtr pUnkOuter);

                void GetCaps(IntPtr caps);

                void DuplicateSoundBuffer([In, MarshalAs(UnmanagedType.Interface)] IDirectSoundBuffer bufferOriginal, [In, MarshalAs(UnmanagedType.Interface)] IDirectSoundBuffer bufferDuplicate);

                void SetCooperativeLevel(IntPtr windowHandle, [In, MarshalAs(UnmanagedType.U4)] DirectSoundCooperativeLevel dwLevel);

                void Compact();

                void GetSpeakerConfig(IntPtr pdwSpeakerConfig);

                void SetSpeakerConfig(uint pdwSpeakerConfig);

                void Initialize([In, MarshalAs(UnmanagedType.LPStruct)] Guid guid);
            }

            /// <summary>
            /// IDirectSoundBuffer interface
            /// </summary>
            [ComImport]
            [Guid("279AFA85-4981-11CE-A521-0020AF0BE560")]
            [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            [SuppressUnmanagedCodeSecurity]
            public interface IDirectSoundBuffer
            {
                void GetCaps([MarshalAs(UnmanagedType.LPStruct)] BufferCaps pBufferCaps);

                void GetCurrentPosition([Out] out uint currentPlayCursor, [Out] out uint currentWriteCursor);

                void GetFormat();

                [return: MarshalAs(UnmanagedType.I4)]
                int GetVolume();

                void GetPan([Out] out uint pan);

                [return: MarshalAs(UnmanagedType.I4)]
                int GetFrequency();

                [return: MarshalAs(UnmanagedType.U4)]
                DirectSoundBufferStatus GetStatus();

                void Initialize([In, MarshalAs(UnmanagedType.Interface)] IDirectSound directSound, [In] BufferDescription desc);

                void Lock(int dwOffset, uint dwBytes, [Out] out IntPtr audioPtr1, [Out] out int audioBytes1, [Out] out IntPtr audioPtr2, [Out] out int audioBytes2, [MarshalAs(UnmanagedType.U4)] DirectSoundBufferLockFlag dwFlags);

                void Play(uint dwReserved1, uint dwPriority, [In, MarshalAs(UnmanagedType.U4)] DirectSoundPlayFlags dwFlags);

                void SetCurrentPosition(uint dwNewPosition);

                void SetFormat([In] WaveFormat pcfxFormat);

                void SetVolume(int volume);

                void SetPan(uint pan);

                void SetFrequency(uint frequency);

                void Stop();

                void Unlock(IntPtr pvAudioPtr1, int dwAudioBytes1, IntPtr pvAudioPtr2, int dwAudioBytes2);

                void Restore();
            }

            /// <summary>
            /// IDirectSoundNotify interface
            /// </summary>
            [ComImport]
            [Guid("b0210783-89cd-11d0-af08-00a0c925cd16")]
            [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            [SuppressUnmanagedCodeSecurity]
            public interface IDirectSoundNotify
            {
                void SetNotificationPositions(uint dwPositionNotifies, [In, MarshalAs(UnmanagedType.LPArray)] DirectSoundBufferPositionNotify[] pcPositionNotifies);
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct DirectSoundBufferPositionNotify
            {
                public uint Offset;
                public IntPtr NotifyHandle;
            }

#pragma warning disable SA1401 // Fields must be private

            [StructLayout(LayoutKind.Sequential, Pack = 2)]
            public class BufferDescription
            {
                public int Size;

                [MarshalAs(UnmanagedType.U4)]
                public DirectSoundBufferCaps Flags;

                public uint BufferBytes;

                public int Reserved;

                public IntPtr FormatHandle;

                public Guid AlgorithmId;
            }

            [StructLayout(LayoutKind.Sequential, Pack = 2)]
            public class BufferCaps
            {
                public int Size;
                public int Flags;
                public int BufferBytes;
                public int UnlockTransferRate;
                public int PlayCpuOverhead;
            }

#pragma warning restore SA1401 // Fields must be private
        }

        private static class NativeMethods
        {
            private const string DirectSoundLib = "dsound.dll";
            private const string User32Lib = "user32.dll";

            /// <summary>
            /// Instanciate DirectSound from the DLL
            /// </summary>
            /// <param name="deviceGuid">The GUID.</param>
            /// <param name="directSound">The direct sound.</param>
            /// <param name="pUnkOuter">The p unk outer.</param>
            /// <returns>The result code</returns>
            [DllImport(DirectSoundLib, EntryPoint = nameof(DirectSoundCreate), SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
            public static extern int DirectSoundCreate(ref Guid deviceGuid, [Out, MarshalAs(UnmanagedType.Interface)] out DirectSound.IDirectSound directSound, IntPtr pUnkOuter);

            /// <summary>
            /// The DirectSoundEnumerate function enumerates the DirectSound drivers installed in the system.
            /// </summary>
            /// <param name="lpDSEnumCallback">callback function</param>
            /// <param name="lpContext">User context</param>
            [DllImport(DirectSoundLib, EntryPoint = nameof(DirectSoundEnumerateA), SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
            public static extern void DirectSoundEnumerateA(DirectSound.EnumerateDevicesDelegate lpDSEnumCallback, IntPtr lpContext);

            /// <summary>
            /// Gets the HANDLE of the desktop window.
            /// </summary>
            /// <returns>HANDLE of the Desktop window</returns>
            [DllImport(User32Lib)]
            public static extern IntPtr GetDesktopWindow();
        }

        #endregion
    }
}
