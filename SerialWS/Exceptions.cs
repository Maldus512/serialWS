using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SerialWS.Exceptions {

    public class InvalidCSVException : Exception {
        public InvalidCSVException() {
        }

        public InvalidCSVException(string message)
            : base(message) {
        }

        public InvalidCSVException(string message, Exception inner)
            : base(message, inner) {
        }
    }
}
