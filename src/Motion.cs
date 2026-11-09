using System;

namespace DialogueFlow
{
    internal sealed class SpeedEnvelope
    {
        internal const float Maximum = 6f;
        private const double Response = 18d;
        private double first = 1d;
        private double second = 1d;
        private double third = 1d;

        public float Value => (float)third;
        public float Velocity => (float)(Response * (second - third));
        public float Acceleration => (float)(Response * Response * (first - 2d * second + third));

        public float Step(bool held, float deltaTime)
        {
            if (!(deltaTime > 0f) || float.IsInfinity(deltaTime)) return Value;
            double target = held ? Maximum : 1d;
            double time = Response * deltaTime;
            double decay = Math.Exp(-time);
            double offsetFirst = first - target;
            double offsetSecond = second - target;
            double offsetThird = third - target;

            // 三级滤波的精确积分让帧率变化时仍保持同一条曲线，切换目标时速度与加速度连续。
            first = target + offsetFirst * decay;
            second = target + (offsetSecond + offsetFirst * time) * decay;
            third = target + (offsetThird + offsetSecond * time + 0.5d * offsetFirst * time * time) * decay;
            return Value;
        }

        public void Reset()
        {
            first = second = third = 1d;
        }
    }

    internal sealed class ElasticSpring
    {
        private const double Frequency = 18d;
        private const double Damping = 0.48d;
        private double position;
        private double velocity;

        public ElasticSpring(float initialValue)
        {
            position = initialValue;
        }

        public float Value => (float)position;

        public float Step(float target, float deltaTime)
        {
            if (!(deltaTime > 0f) || float.IsInfinity(deltaTime)) return Value;
            double decayRate = Damping * Frequency;
            double oscillation = Frequency * Math.Sqrt(1d - Damping * Damping);
            double angle = oscillation * deltaTime;
            double decay = Math.Exp(-decayRate * deltaTime);
            double offset = position - target;
            double sineCoefficient = (velocity + decayRate * offset) / oscillation;
            double cosine = Math.Cos(angle);
            double sine = Math.Sin(angle);
            double wave = offset * cosine + sineCoefficient * sine;

            // 保存位置与速度，使松键和新句到来时的回弹可以从当前状态接续。
            position = target + decay * wave;
            velocity = decay * (-decayRate * wave - offset * oscillation * sine
                + sineCoefficient * oscillation * cosine);
            return Value;
        }

        public void Reset(float value)
        {
            position = value;
            velocity = 0d;
        }
    }
}
