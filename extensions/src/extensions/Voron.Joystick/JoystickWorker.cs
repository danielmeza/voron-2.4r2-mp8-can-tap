
using System.Device.Gpio;

using Microsoft.Extensions.Hosting;

namespace Voron.Joystick
{
    internal class JoystickWorker : IHostedService
    {
        private GpioController _gpioController;

        public JoystickWorker(GpioController gpioController)
        {
            _gpioController = gpioController;
        }

        private void Configure()
        {
            _gpioController.SetPinMode(23, PinMode.InputPullUp);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Configure();
            throw new NotImplementedException();
        }


        public Task StopAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
