using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using bbv.Common.EventBroker;
using bbv.Common.EventBroker.Internals;

namespace RadarStreamerPlugin
{
    /// <summary>A VATSIM Velocity position report as received by vPilot.</summary>
    internal struct FastPosition
    {
        public string Callsign;
        public double Lat, Lon, AltFt;
        public double? AglFt;
        /// <summary>World velocities in m/s: east, up, north (FSD "^" packet order lon/alt/lat).</summary>
        public double VelEast, VelUp, VelNorth;
        /// <summary>Rotational rates in rad/s as sent on the wire (pitch, heading, bank).</summary>
        public double RotX, RotY, RotZ;
    }

    /// <summary>
    /// Taps vPilot's internal event broker for the "FastPositionUpdateReceived" topic.
    ///
    /// The public plugin API raises AircraftUpdated only on 5-second slow updates; the 5 Hz
    /// Velocity packets (^, #SL, #ST) are consumed internally. vPilot is obfuscated, so the
    /// event-args members are located by type and declaration order rather than by name:
    ///   args  : NetworkDataReceivedEventArgs.From, then [VisualState, Vector3 positional, Vector3 rotational]
    ///   state : [WorldPoint, double altitude, double? agl, ...attitude]
    /// Every step is validated; on any mismatch the hook disables itself and the plugin
    /// falls back to the public slow-update events.
    /// </summary>
    internal sealed class FastPositionHook : IEventBrokerExtension
    {
        public const string Topic = "FastPositionUpdateReceived";

        private readonly Action<FastPosition> _onPosition;
        private readonly Action<string> _log;
        private IEventBroker _broker;
        private Accessors _acc;
        private Type _accType;
        private int _failures;
        private bool _disabled;

        private FastPositionHook(Action<FastPosition> onPosition, Action<string> log)
        {
            _onPosition = onPosition;
            _log = log;
        }

        /// <summary>Installs the hook on the broker object vPilot hands to plugins. Returns null on failure.</summary>
        public static FastPositionHook TryInstall(object pluginBroker, Action<FastPosition> onPosition, Action<string> log)
        {
            try
            {
                var field = pluginBroker.GetType()
                    .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .FirstOrDefault(f => typeof(IEventBroker).IsAssignableFrom(f.FieldType));
                if (field == null)
                {
                    log("Fast hook: event broker not found on " + pluginBroker.GetType().Name + "; using slow updates only.");
                    return null;
                }
                var broker = (IEventBroker)field.GetValue(pluginBroker);
                if (broker == null) return null;

                var hook = new FastPositionHook(onPosition, log) { _broker = broker };
                broker.AddExtension(hook);
                return hook;
            }
            catch (Exception ex)
            {
                log("Fast hook install failed: " + ex.Message + "; using slow updates only.");
                return null;
            }
        }

        public bool IsActive => !_disabled;

        public void Uninstall()
        {
            try { _broker?.RemoveExtension(this); } catch { }
            _broker = null;
        }

        public void FiringEvent(IEventTopic eventTopic, IPublication publication, object sender, EventArgs e)
        {
            if (_disabled || e == null || eventTopic == null || eventTopic.Uri != Topic) return;
            try
            {
                var t = e.GetType();
                if (t != _accType)
                {
                    _acc = Accessors.Resolve(t);
                    _accType = t;
                }
                if (_acc.TryRead(e, out var pos))
                {
                    _failures = 0;
                    _onPosition(pos);
                }
                else Fail("fast position failed validation");
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
            }
        }

        private void Fail(string why)
        {
            // A few bad packets can be data; a run of them means the layout changed.
            if (++_failures < 20) return;
            _disabled = true;
            _log("Fast hook disabled (" + why + "). vPilot internals may have changed; using slow updates only.");
            Uninstall();
        }

        /// <summary>Reflection accessors for one obfuscated event-args type.</summary>
        private sealed class Accessors
        {
            private PropertyInfo _from;
            private FieldInfo _state, _posVel, _rotVel;
            private FieldInfo _point, _alt, _agl;
            private MemberInfo _lat, _lon;
            private MemberInfo _vx, _vy, _vz;

            public static Accessors Resolve(Type argsType)
            {
                var a = new Accessors();
                a._from = argsType.GetProperty("From", BindingFlags.Instance | BindingFlags.Public)
                          ?? throw new MissingMemberException("From");

                var fields = InstanceFields(argsType);
                var vectors = fields.Where(f => f.FieldType.Name == "Vector3").ToList();
                if (vectors.Count != 2) throw new MissingMemberException("expected 2 Vector3 fields, found " + vectors.Count);
                a._posVel = vectors[0];
                a._rotVel = vectors[1];

                a._state = fields.FirstOrDefault(f => InstanceFields(f.FieldType).Any(sf => sf.FieldType.Name == "WorldPoint"))
                           ?? throw new MissingMemberException("visual state");

                var stateFields = InstanceFields(a._state.FieldType);
                a._point = stateFields.First(f => f.FieldType.Name == "WorldPoint");
                a._alt = stateFields.FirstOrDefault(f => f.FieldType == typeof(double))
                         ?? throw new MissingMemberException("altitude");
                a._agl = stateFields.FirstOrDefault(f => f.FieldType == typeof(double?));

                a._lat = Member(a._point.FieldType, "Lat");
                a._lon = Member(a._point.FieldType, "Lon");
                a._vx = Member(a._posVel.FieldType, "X");
                a._vy = Member(a._posVel.FieldType, "Y");
                a._vz = Member(a._posVel.FieldType, "Z");
                return a;
            }

            public bool TryRead(object args, out FastPosition p)
            {
                p = default;
                var state = _state.GetValue(args);
                if (state == null) return false;
                var point = _point.GetValue(state);
                var pos = _posVel.GetValue(args);
                var rot = _rotVel.GetValue(args);

                p.Callsign = (string)_from.GetValue(args);
                p.Lat = Get(_lat, point);
                p.Lon = Get(_lon, point);
                p.AltFt = (double)_alt.GetValue(state);
                p.AglFt = _agl != null ? (double?)_agl.GetValue(state) : null;
                p.VelEast = Get(_vx, pos);
                p.VelUp = Get(_vy, pos);
                p.VelNorth = Get(_vz, pos);
                p.RotX = Get(_vx, rot);
                p.RotY = Get(_vy, rot);
                p.RotZ = Get(_vz, rot);

                return !string.IsNullOrEmpty(p.Callsign)
                       && p.Lat >= -90 && p.Lat <= 90 && p.Lon >= -180 && p.Lon <= 180
                       && p.AltFt > -2000 && p.AltFt < 100000
                       && Math.Abs(p.VelEast) < 1000 && Math.Abs(p.VelUp) < 1000 && Math.Abs(p.VelNorth) < 1000;
            }

            private static List<FieldInfo> InstanceFields(Type t) =>
                t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                 .OrderBy(f => f.MetadataToken)
                 .ToList();

            private static MemberInfo Member(Type t, string name) =>
                (MemberInfo)t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
                ?? t.GetField(name, BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingMemberException(t.Name + "." + name);

            private static double Get(MemberInfo m, object o) =>
                Convert.ToDouble(m is PropertyInfo pi ? pi.GetValue(o) : ((FieldInfo)m).GetValue(o));
        }

        // Unused extension callbacks.
        public void FiredEvent(IEventTopic eventTopic, IPublication publication, object sender, EventArgs e) { }
        public void RegisteredItem(object item) { }
        public void UnregisteredItem(object item) { }
        public void ProcessedPublisher(object publisher, bool register, IEventTopicHost eventTopicHost) { }
        public void ProcessedSubscriber(object subscriber, bool register, IEventTopicHost eventTopicHost) { }
        public void CreatedTopic(IEventTopic eventTopic) { }
        public void CreatedPublication(IEventTopic eventTopic, IPublication publication) { }
        public void CreatedSubscription(IEventTopic eventTopic, ISubscription subscription) { }
        public void AddedPublication(IEventTopic eventTopic, IPublication publication) { }
        public void RemovedPublication(IEventTopic eventTopic, IPublication publication) { }
        public void AddedSubscription(IEventTopic eventTopic, ISubscription subscription) { }
        public void RemovedSubscription(IEventTopic eventTopic, ISubscription subscription) { }
        public void Disposed(IEventTopic eventTopic) { }
        public void SubscriberExceptionsOccurred(IEventTopic eventTopic, IEnumerable<Exception> exceptions) { }
        public void RelayingEvent(IEventTopic eventTopic, IPublication publication, ISubscription subscription, IHandler handler, object sender, EventArgs e) { }
        public void RelayedEvent(IEventTopic eventTopic, IPublication publication, ISubscription subscription, IHandler handler, object sender, EventArgs e, Exception exception) { }
        public void SkippedEvent(IEventTopic eventTopic, IPublication publication, ISubscription subscription, object sender, EventArgs e) { }
    }
}
