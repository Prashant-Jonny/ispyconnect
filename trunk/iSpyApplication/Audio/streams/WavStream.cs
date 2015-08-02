﻿using System;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using AForge.Video;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace iSpyApplication.Audio.streams
{
    public class WavStream : IAudioSource
    {
        private string _source;
        private ManualResetEvent _stopEvent = null;
        private bool _listening;

        private Thread _thread;
        private BufferedWaveProvider _waveProvider;
        private SampleChannel _sampleChannel;
        public BufferedWaveProvider WaveOutProvider { get; set; }

        private float _gain;

        public WaveFormat RecordingFormat { get; set; }

        public float Gain
        {
            get { return _gain; }
            set
            {
                _gain = value;
                if (_sampleChannel != null)
                {
                    _sampleChannel.Volume = value;
                }
            }
        }
        public bool Listening
        {
            get
            {
                if (IsRunning && _listening)
                    return true;
                return false;

            }
            set
            {
                if (RecordingFormat == null)
                {
                    _listening = false;
                    return;
                }

                if (WaveOutProvider != null)
                {
                    if (WaveOutProvider.BufferedBytes > 0) WaveOutProvider.ClearBuffer();
                    WaveOutProvider = null;
                }
                if (value)
                {
                    WaveOutProvider = new BufferedWaveProvider(RecordingFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(500) };
                }

                _listening = value;
            }
        }

        /// <summary>
        /// New frame event.
        /// </summary>
        /// 
        /// <remarks><para>Notifies clients about new available frame from audio source.</para>
        /// 
        /// <para><note>Since audio source may have multiple clients, each client is responsible for
        /// making a copy (cloning) of the passed audio frame, because the audio source disposes its
        /// own original copy after notifying of clients.</note></para>
        /// </remarks>
        /// 
        public event DataAvailableEventHandler DataAvailable;

        /// <summary>
        /// New frame event.
        /// </summary>
        /// 
        /// <remarks><para>Notifies clients about new available frame from audio source.</para>
        /// 
        /// <para><note>Since audio source may have multiple clients, each client is responsible for
        /// making a copy (cloning) of the passed audio frame, because the audio source disposes its
        /// own original copy after notifying of clients.</note></para>
        /// </remarks>
        /// 
        public event LevelChangedEventHandler LevelChanged;

        /// <summary>
        /// audio source error event.
        /// </summary>
        /// 
        /// <remarks>This event is used to notify clients about any type of errors occurred in
        /// audio source object, for example internal exceptions.</remarks>
        /// 
        public event AudioSourceErrorEventHandler AudioSourceError;

        /// <summary>
        /// audio playing finished event.
        /// </summary>
        /// 
        /// <remarks><para>This event is used to notify clients that the audio playing has finished.</para>
        /// </remarks>
        /// 
        public event AudioFinishedEventHandler AudioFinished;

        /// <summary>
        /// audio source.
        /// </summary>
        /// 
        /// <remarks>URL, which provides JPEG files.</remarks>
        /// 
        public virtual string Source
        {
            get { return _source; }
            set { _source = value; }
        }


        /// <summary>
        /// State of the audio source.
        /// </summary>
        /// 
        /// <remarks>Current state of audio source object - running or not.</remarks>
        /// 
        public bool IsRunning
        {
            get
            {
                if (_thread != null)
                {
                    // check thread status
                    if (!_thread.Join(TimeSpan.Zero))
                        return true;

                    // the thread is not running, free resources
                    Free();
                }
                return false;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalDeviceStream"/> class.
        /// </summary>
        /// 
        /// <param name="source">source, which provides audio data.</param>
        /// 
        public WavStream(string source)
        {
            _source = source;
        }


        /// <summary>
        /// Start audio source.
        /// </summary>
        /// 
        /// <remarks>Starts audio source and return execution to caller. audio source
        /// object creates background thread and notifies about new frames with the
        /// help of <see cref="DataAvailable"/> event.</remarks>
        /// 
        /// <exception cref="ArgumentException">audio source is not specified.</exception>
        /// 
        public void Start()
        {
            if (!IsRunning)
            {
                // check source
                if (string.IsNullOrEmpty(_source))
                    throw new ArgumentException("Audio source is not specified.");

                _waveProvider = new BufferedWaveProvider(RecordingFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(500) };
                _sampleChannel = new SampleChannel(_waveProvider);
                _sampleChannel.PreVolumeMeter += SampleChannelPreVolumeMeter;

                _stopEvent = new ManualResetEvent(false);
                _thread = new Thread(StreamWav)
                {
                    Name = "WavStream Audio Receiver (" + _source + ")"
                };
                _thread.Start();

            }
        }



        private void StreamWav()
        {
            var res = ReasonToFinishPlaying.StoppedByUser;
            HttpWebRequest request = null;
            try
            {
                using (HttpWebResponse resp = ConnectionFactory.GetResponse(_source, out request))
                {
                    //1/10 of a second, 16 byte buffer
                    var data = new byte[((RecordingFormat.SampleRate / 4) * 2) * RecordingFormat.Channels];

                    using (var stream = resp.GetResponseStream())
                    {
                        if (stream == null)
                            throw new Exception("Stream is null");

                        while (!_stopEvent.WaitOne(10, false) && !MainForm.ShuttingDown)
                        {
                            var da = DataAvailable;
                            if (da != null)
                            {
                                int recbytesize = stream.Read(data, 0, data.Length);
                                if (recbytesize == 0)
                                    throw new Exception("lost stream");


                                if (_sampleChannel != null)
                                {
                                    _waveProvider.AddSamples(data, 0, recbytesize);

                                    var sampleBuffer = new float[recbytesize];
                                    _sampleChannel.Read(sampleBuffer, 0, recbytesize);

                                    if (Listening && WaveOutProvider != null)
                                    {
                                        WaveOutProvider.AddSamples(data, 0, recbytesize);
                                    }
                                    var dae = new DataAvailableEventArgs((byte[])data.Clone(), recbytesize);
                                    da(this, dae);
                                }
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                }

                if (AudioFinished != null)
                    AudioFinished(this, ReasonToFinishPlaying.StoppedByUser);
            }
            catch (Exception ex)
            {
                var af = AudioFinished;
                if (af != null)
                    af(this, ReasonToFinishPlaying.DeviceLost);

                MainForm.LogExceptionToFile(ex, "WavStream");
            }
            finally
            {
                // abort request
                if (request != null)
                {
                    try
                    {
                        request.Abort();
                    }
                    catch { }
                    request = null;
                }
            }
        }

        void SampleChannelPreVolumeMeter(object sender, StreamVolumeEventArgs e)
        {
            if (LevelChanged != null)
            {
                LevelChanged(this, new LevelChangedEventArgs(e.MaxSampleValues));
            }
        }

        /// <summary>
        /// Stop audio source.
        /// </summary>
        /// 
        /// <remarks><para>Stops audio source.</para>
        /// </remarks>
        /// 
        public void Stop()
        {
            if (IsRunning)
            {
                _stopEvent.Set();
                try
                {
                    while (_thread != null && !_thread.Join(0))
                        Application.DoEvents();
                }
                catch { }

                Free();
            }
        }

        /// <summary>
        /// Free resource.
        /// </summary>
        /// 
        private void Free()
        {
            _thread = null;

            // release events
            if (_stopEvent != null)
            {
                _stopEvent.Close();
                _stopEvent.Dispose();
            }
            _stopEvent = null;
        }

    }
}
